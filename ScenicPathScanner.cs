using System.Collections.Generic;
using ColossalFramework;
using ICities;
using TakeAWalk.Util;
using UnityEngine;

namespace TakeAWalk
{
    // The heart of the mod (v2). Two decoupled phases:
    //
    //  • Scoring (throttled, SegmentsPerTick): round-robin over the segment buffer, score each
    //    pedestrian path, measure its contiguous length, and store {pos, rate} in a cache.
    //  • Injection (EVERY tick): re-add Entertainment for every cached point. This is the fix
    //    for the "no visible boost" bug - the immaterial-resource grid zeroes temp cells each
    //    cycle, so an effect must be re-applied continuously (like a real park) to persist.
    //
    // Half 2 (drawing cims onto dead-end paths via a transient attraction) is added separately.
    public class ScenicPathScanner : ThreadingExtensionBase
    {
        private struct CachePoint
        {
            public Vector3 Pos;
            public int Rate;
            public int Sweep;   // last sweep this point was re-confirmed (for eviction)
        }

        private static uint _cursor;
        private static int _sweep;
        private static readonly Dictionary<ushort, CachePoint> _cache = new Dictionary<ushort, CachePoint>(2048);
        private static readonly List<ushort> _evict = new List<ushort>(256);

        // Half 2 - walking tours. A path is "near a road" (an entrance) within this many metres.
        private const float RoadProximity = 32f;
        private static readonly List<Vector3> _stops = new List<Vector3>(16);  // scratch route buffer
        // Per-sweep cache: segment id -> its component's contiguous length. Doubles as the
        // once-per-component dedup (a key present means the component was already measured/toured).
        private static readonly Dictionary<ushort, float> _segLen = new Dictionary<ushort, float>(4096);

        // ── Debug telemetry (only touched when Log.DebugEnabled) ──────────────────
        private const int SummaryEveryTicks = 586;
        private static int _ticks;
        private static int _pathsSeen;
        private static int _maxRate;
        private static float _maxLen;
        private static int _toursSeen;      // single-entrance paths that qualified for a tour
        private static int _throughPath;    // rejected: through-path (2+ road connections)
        private static int _tooShort;       // rejected: reachable but below length/stop minimum
        private static int _noEntrance;     // rejected: unreachable island (0 road connections)
        private static int _noHousing;      // rejected: no homes near the path entrance
        private static bool _sampleLogged;
        private static bool _rejSampleLogged;
        private static bool _announcedLive;

        // Set once each when a sim-thread entry point first throws, so we log a real managed stack
        // (instead of CS1 dying natively on an unhandled simulation-thread exception) without
        // spamming the log every tick afterwards.
        private static bool _tickErrorLogged;
        private static bool _updateErrorLogged;

        public override void OnCreated(IThreading threading)
        {
            base.OnCreated(threading);
            Log.Info("Scenic path scanner active v2 (segments/tick=" +
                     Settings.Instance.SegmentsPerTick + ", debug=" + Log.DebugEnabled + ")");
        }

        public override void OnReleased()
        {
            WalkingTourManager.ReleaseAll();
            _cache.Clear();
            _segLen.Clear();
            _cursor = 0;
            _sweep = 0;
            _announcedLive = false;
            base.OnReleased();
        }

        // Per-frame (even while paused): commit freshly built walking-tour line paths.
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            try
            {
                WalkingTourManager.Tick();
            }
            catch (System.Exception e)
            {
                if (!_updateErrorLogged)
                {
                    _updateErrorLogged = true;
                    Log.Error("OnUpdate/Tick threw (suppressing further reports): " + e);
                }
            }
        }

        public override void OnAfterSimulationTick()
        {
            try
            {
                DoSimulationTick();
            }
            catch (System.Exception e)
            {
                if (!_tickErrorLogged)
                {
                    _tickErrorLogged = true;
                    Log.Error("OnAfterSimulationTick threw (suppressing further reports): " + e);
                }
            }
        }

        private static void DoSimulationTick()
        {
            Settings s = Settings.Instance;
            if (!s.Enabled) { if (_cache.Count > 0) _cache.Clear(); return; }
            if (!Singleton<NetManager>.exists) return;

            NetManager nm = Singleton<NetManager>.instance;

            // Monthly: rotate the tour set (retire all, re-roll over the coming month).
            if (s.EnableWalkingTours) WalkingTourManager.MaybeRegen();

            // Phase 1 - scoring (throttled).
            ScorePass(nm, s);

            // Phase 2 - injection (every tick, from cache).
            InjectPass(s);

            // Appeal: injected EVERY tick (like the leisure value, which is smooth and visible) so
            // it sustains a stable value - but only at the few tour points, so it stays a local draw
            // and never carpets the map the way per-path-point injection did.
            if (s.EnableWalkingTours) WalkingTourManager.InjectAppeal(s);

            if (Log.DebugEnabled)
            {
                if (!_announcedLive && _cache.Count > 0)
                {
                    _announcedLive = true;
                    Log.DebugLog("injection live - " + _cache.Count +
                        " scenic path point(s) feeding Entertainment every tick");
                }
                if (++_ticks >= SummaryEveryTicks)
                    FlushSummary();
            }
        }

        private static void ScorePass(NetManager nm, Settings s)
        {
            NetSegment[] segments = nm.m_segments.m_buffer;
            uint count = nm.m_segments.m_size;
            if (count == 0) return;

            int budget = s.SegmentsPerTick;
            for (int i = 0; i < budget; i++)
            {
                if (_cursor >= count)
                {
                    _cursor = 0;
                    EvictStale();   // completed a full sweep
                    if (s.EnableWalkingTours) WalkingTourManager.EvictStale(_sweep);
                    _segLen.Clear();
                    _sweep++;
                }
                ushort id = (ushort)_cursor++;
                ScoreSegment(nm, ref segments[id], id, s);
            }
        }

        private static void ScoreSegment(NetManager nm, ref NetSegment segment, ushort id, Settings s)
        {
            if ((segment.m_flags & NetSegment.Flags.Created) == 0) { _cache.Remove(id); return; }
            if ((segment.m_flags & NetSegment.Flags.Deleted) != 0) { _cache.Remove(id); return; }
            if (!PathMetrics.IsPedestrianPath(segment.Info)) { _cache.Remove(id); return; }

            Vector3 pos = segment.m_middlePosition;
            ScenicResult scenic = ScenicScore.Evaluate(pos);
            if (!scenic.Eligible) { _cache.Remove(id); return; }

            // Measure the contiguous path length once per component per sweep (cached against every
            // segment of the component). On the first (cache-miss) segment we also build the tour
            // candidate so it can be considered once we know the path's value.
            float length;
            bool freshComponent = false;
            ushort tip = 0;
            int entrances = 0;
            bool toursOn = s.EnableWalkingTours && WalkingTourManager.ParklifeAvailable();
            float cachedLen;
            if (_segLen.TryGetValue(id, out cachedLen))
            {
                length = cachedLen;
            }
            else
            {
                freshComponent = true;
                tip = PathMetrics.TryBuildTour(nm, id, s.MaxPathSegments, s.MaxStops,
                    RoadProximity, _stops, _segLen, toursOn, out length, out entrances);
            }

            int rate = ComputeRate(scenic.Score, length, s);
            if (rate < 1) { _cache.Remove(id); return; }   // too small/bare to be a leisure zone

            // Half 2 - create/refresh a walking tour for this component (once per sweep, on its first
            // segment). goodness = how appealing the path is, used to weight the creation chance.
            if (freshComponent && toursOn)
            {
                float maxRate = s.SmallParkRate * s.MaxTotalMultiplier;
                float goodness = maxRate > 0f ? rate / maxRate : 0f;
                if (tip != 0 && length >= s.TourMinLength && _stops.Count >= 3)
                {
                    if (WalkingTourManager.HasHousingNear(_stops[0], s.TourHousingRadius))
                    {
                        if (Log.DebugEnabled) _toursSeen++;
                        WalkingTourManager.Consider(tip, _stops, length, goodness, _sweep, s);
                    }
                    else if (Log.DebugEnabled)
                    {
                        _noHousing++;
                        if (!_rejSampleLogged)
                        {
                            _rejSampleLogged = true;
                            Log.DebugLog("sample tour-reject @ (" + pos.x.ToString("F0") + "," +
                                pos.z.ToString("F0") + ") len=" + length.ToString("F0") +
                                "m - no housing within " + s.TourHousingRadius.ToString("F0") + "m");
                        }
                    }
                }
                else if (Log.DebugEnabled)
                {
                    if (entrances >= 2) _throughPath++;   // already used by pedestrians - skipped
                    else if (entrances == 0) _noEntrance++;
                    else _tooShort++;   // reachable, but below the length / stop minimum
                    if (!_rejSampleLogged && entrances >= 1)
                    {
                        _rejSampleLogged = true;
                        Log.DebugLog("sample tour-reject @ (" + pos.x.ToString("F0") + "," +
                            pos.z.ToString("F0") + ") entrances=" + entrances + " len=" +
                            length.ToString("F0") + "m stops=" + _stops.Count +
                            " (need len>=" + s.TourMinLength.ToString("F0") + " & stops>=3)");
                    }
                }
            }

            CachePoint cp;
            cp.Pos = pos;
            cp.Rate = rate;
            cp.Sweep = _sweep;
            _cache[id] = cp;

            if (Log.DebugEnabled)
            {
                _pathsSeen++;
                if (rate > _maxRate) _maxRate = rate;
                if (length > _maxLen) _maxLen = length;
                if (!_sampleLogged)
                {
                    _sampleLogged = true;
                    Log.DebugLog("sample path @ (" + pos.x.ToString("F0") + "," + pos.z.ToString("F0") +
                        ") len=" + length.ToString("F0") + "m quality=" + scenic.Score.ToString("F0") +
                        " [trees=" + scenic.Trees + " props=" + scenic.Props + " noise=" + scenic.Noise +
                        " water=" + scenic.Water + "] -> rate " + rate);
                }
            }
        }

        // rate = SmallParkRate · clamp(lengthFactor + decorationFactor, 0, MaxTotalMultiplier).
        // Additive: length and decoration each earn value, so a long path OR a richly decorated
        // one is park-like, while a short bare path comes out ~0 (caller skips it).
        private static int ComputeRate(float quality, float lengthMeters, Settings s)
        {
            float lengthFactor = lengthMeters / s.LengthForFullValue;
            float decorationFactor = s.DecorationForFullValue > 0f ? quality / s.DecorationForFullValue : 0f;

            float mult = lengthFactor + decorationFactor;
            if (mult > s.MaxTotalMultiplier) mult = s.MaxTotalMultiplier;

            return Mathf.RoundToInt(s.SmallParkRate * mult);
        }

        private static void InjectPass(Settings s)
        {
            if (_cache.Count == 0) return;
            ImmaterialResourceManager irm = Singleton<ImmaterialResourceManager>.instance;
            float radius = s.InjectRadius;
            bool sight = s.InjectSightseeing;
            float healthShare = s.HealthShare;

            foreach (CachePoint cp in _cache.Values)
            {
                irm.AddResource(ImmaterialResourceManager.Resource.Entertainment, cp.Rate, cp.Pos, radius);
                if (sight)
                {
                    int half = cp.Rate / 2;
                    if (half < 1) half = 1;
                    irm.AddResource(ImmaterialResourceManager.Resource.Sightseeing, half, cp.Pos, radius);
                    irm.AddResource(ImmaterialResourceManager.Resource.TourCoverage, half, cp.Pos, radius);
                }
                if (healthShare > 0f)
                {
                    int health = Mathf.RoundToInt(cp.Rate * healthShare);
                    if (health >= 1)
                        irm.AddResource(ImmaterialResourceManager.Resource.Health, health, cp.Pos, radius);
                }
                // Appeal is NOT injected per point here (it carpets the whole network); it is added
                // once per tour at the tour's start - see WalkingTourManager.InjectAppeal.
            }
        }

        // Drop cache entries not re-confirmed during the sweep just completed.
        private static void EvictStale()
        {
            _evict.Clear();
            foreach (KeyValuePair<ushort, CachePoint> kv in _cache)
                if (kv.Value.Sweep < _sweep) _evict.Add(kv.Key);
            for (int i = 0; i < _evict.Count; i++)
                _cache.Remove(_evict[i]);
        }

        private static void FlushSummary()
        {
            Log.DebugLog("summary (last " + _ticks + " ticks): cache " + _cache.Count +
                " scenic paths injected/tick; scored " + _pathsSeen + " this window, " +
                "max length " + _maxLen.ToString("F0") + "m, max rate " + _maxRate +
                "; tour-eligible " + _toursSeen + " (reject through-path=" + _throughPath +
                " too-short=" + _tooShort + " isolated=" + _noEntrance + " no-housing=" + _noHousing +
                "); live walking tours " + WalkingTourManager.Count);
            WalkingTourManager.LogUsage();
            _ticks = 0;
            _pathsSeen = 0;
            _maxRate = 0;
            _maxLen = 0f;
            _toursSeen = 0;
            _throughPath = 0;
            _tooShort = 0;
            _noEntrance = 0;
            _noHousing = 0;
            _sampleLogged = false;
            _rejSampleLogged = false;
        }
    }
}
