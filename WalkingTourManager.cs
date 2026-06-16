using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using TakeAWalk.Util;
using UnityEngine;

namespace TakeAWalk
{
    // Half 2 (v3). Creates native walking-tour TransportLines (Pedestrian transport) along scenic
    // single-entrance paths, so the game itself spawns walking groups that stroll them - cims
    // spending leisure time WALKING the path, which is the whole point.
    //
    // Lines are transient: capped in number, re-confirmed each scan sweep, and EXCLUDED FROM THE
    // SAVE by a Harmony patch on TransportManager.Data.Serialize (see TransportLineSerializePatch).
    // Released on level-unload / mod-disable. Walking tours are a Parklife (ParksDLC) feature; when
    // it is not owned this whole half stays disabled. Line creation mirrors the proven pattern from
    // the sibling SchoolBuses mod (CreateLine -> AddStop -> append-close -> UpdateLinesNow window).
    //
    // Simulation-thread only (called from ScenicPathScanner), except Tick() which is per-frame.
    internal static class WalkingTourManager
    {
        private struct Tour
        {
            public ushort Line;
            public int LastSweep;
            public float Length;     // route length when last built, to detect a path being extended
            public float Goodness;   // how appealing the path is (for displacing weaker tours)
            public Vector3 AppealA;  // two points along the route where appeal is injected
            public Vector3 AppealB;
        }

        // Our tours are drawn a distinct brown so they're easy to tell apart from the player's own
        // lines (and we only ever recolour/rebuild/evict lines in our own registry).
        private static readonly Color32 TourColor = new Color32(150, 111, 51, 255);

        // When a tour slot is free, a path becomes a tour with this goodness-weighted chance, so
        // less-appealing paths still win sometimes. When the cap is full, a NEW path can still take
        // a slot immediately by displacing the weakest tour, but only if it is clearly better
        // (by DisplaceMargin) - so a path you just built gets a tour without waiting for the monthly
        // re-roll, while avoiding constant churn.
        private const double BaseChance = 0.2;
        private const double GoodnessScale = 0.6;
        private const float DisplaceMargin = 0.1f;

        private static readonly Dictionary<ushort, Tour> _byTip = new Dictionary<ushort, Tour>(64);
        private static readonly HashSet<ushort> _lineIds = new HashSet<ushort>();
        private static readonly List<ushort> _evict = new List<ushort>(32);
        private static readonly List<KeyValuePair<ushort, TransportLine.Flags>> _stashed =
            new List<KeyValuePair<ushort, TransportLine.Flags>>(64);
        private static readonly System.Random _rng = new System.Random();

        private static int _finalizeFrames; // while > 0, commit line paths each frame
        private static System.DateTime _lastRegen = System.DateTime.MinValue;

        private static TransportInfo _info;
        private static bool _infoSearched;
        private static int _parklife = -1; // -1 unknown, 0 no, 1 yes

        internal static int Count { get { return _byTip.Count; } }

        // Walking tours need Parklife. Cached after the first query.
        internal static bool ParklifeAvailable()
        {
            if (_parklife < 0)
            {
                bool owned;
                try { owned = SteamHelper.IsDLCOwned(SteamHelper.DLC.ParksDLC); }
                catch { owned = false; }
                _parklife = owned ? 1 : 0;
                Log.Info("Walking tours: Parklife " +
                         (owned ? "present - Half 2 enabled" : "absent - Half 2 disabled"));
            }
            return _parklife == 1;
        }

        internal static bool IsTourLine(ushort lineId) { return _lineIds.Contains(lineId); }

        // Is there housing near pos? Walking groups come from the population, so a tour with no
        // homes nearby (a path out in the wilderness) would never draw anyone - skip those.
        internal static bool HasHousingNear(Vector3 pos, float radius)
        {
            BuildingManager bm = Singleton<BuildingManager>.instance;
            ushort[] grid = bm.m_buildingGrid;
            Building[] buf = bm.m_buildings.m_buffer;
            float r2 = radius * radius;
            const int Res = 270;
            const float Cell = 64f;
            int minX = Clamp((int)((pos.x - radius) / Cell + Res / 2), Res);
            int maxX = Clamp((int)((pos.x + radius) / Cell + Res / 2), Res);
            int minZ = Clamp((int)((pos.z - radius) / Cell + Res / 2), Res);
            int maxZ = Clamp((int)((pos.z + radius) / Cell + Res / 2), Res);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    ushort id = grid[z * Res + x];
                    int guard = 0;
                    while (id != 0 && guard++ < 49152)
                    {
                        if ((buf[id].m_flags & Building.Flags.Created) != 0 &&
                            (buf[id].m_flags & Building.Flags.Deleted) == 0)
                        {
                            BuildingInfo info = buf[id].Info;
                            if (info != null && info.m_class != null &&
                                info.m_class.m_service == ItemClass.Service.Residential)
                            {
                                float dx = buf[id].m_position.x - pos.x;
                                float dz = buf[id].m_position.z - pos.z;
                                if (dx * dx + dz * dz <= r2) return true;
                            }
                        }
                        id = buf[id].m_nextGridBuilding;
                    }
                }
            }
            return false;
        }

        private static int Clamp(int v, int res)
        {
            if (v < 0) return 0;
            if (v > res - 1) return res - 1;
            return v;
        }

        // Monthly rotation: once an in-game month passes, retire all our tours so a fresh, re-rolled
        // set is created over the following month. A month (not a week) gives cims time to actually
        // take a tour before it is replaced. Only OUR lines are touched. Call once per tick.
        internal static void MaybeRegen()
        {
            if (!Singleton<SimulationManager>.exists) return;
            System.DateTime now = Singleton<SimulationManager>.instance.m_currentGameTime;
            if (_lastRegen == System.DateTime.MinValue) { _lastRegen = now; return; }
            if ((now - _lastRegen).TotalDays < 30.0) return;
            if (_byTip.Count > 0)
                Log.DebugLog("monthly regen: retiring " + _byTip.Count + " tour(s) to re-roll");
            ReleaseAll();        // clears tours + resets _lastRegen
            _lastRegen = now;    // start the next month from here
        }

        // Create or refresh a walking tour for a single-entrance path, keyed by its entrance node.
        // goodness (0..1) is how appealing the path is.
        internal static void Consider(ushort key, List<Vector3> stops, float length, float goodness, int sweep, Settings s)
        {
            Tour t;
            if (_byTip.TryGetValue(key, out t))
            {
                t.LastSweep = sweep;
                t.Goodness = goodness;
                // The path grew/shrank (e.g. a loop being drawn) - rebuild the route to cover it now.
                if (Mathf.Abs(length - t.Length) > Mathf.Max(16f, t.Length * 0.1f) &&
                    RebuildStops(t.Line, stops))
                {
                    t.Length = length;
                    SetAppeal(ref t, stops);
                    Log.DebugLog("walking tour " + t.Line + " rebuilt to len " +
                                 Mathf.RoundToInt(length) + "m, " + stops.Count + " stops");
                }
                _byTip[key] = t;
                return;
            }

            if (_byTip.Count < s.MaxTours)
            {
                // Free slot: create with a goodness-weighted chance (lesser paths still win sometimes).
                double chance = BaseChance + GoodnessScale * Mathf.Clamp01(goodness);
                if (_rng.NextDouble() <= chance)
                    CreateTour(key, stops, length, goodness, sweep);
                return;
            }

            // Cap full: a freshly built/updated path can still take a slot right away by displacing
            // the weakest current tour, but only if it is clearly better (avoids constant churn and
            // means you don't wait for the monthly re-roll for a great new path).
            ushort weakestKey;
            float weakestGoodness;
            if (FindWeakest(out weakestKey, out weakestGoodness) &&
                goodness > weakestGoodness + DisplaceMargin)
            {
                Release(_byTip[weakestKey].Line);
                _byTip.Remove(weakestKey);
                Log.DebugLog("walking tour displaced weakest (goodness " + weakestGoodness.ToString("F2") +
                             ") for a better path (goodness " + goodness.ToString("F2") + ")");
                CreateTour(key, stops, length, goodness, sweep);
            }
        }

        private static void CreateTour(ushort key, List<Vector3> stops, float length, float goodness, int sweep)
        {
            TransportInfo info = GetInfo();
            if (info == null) return;
            ushort line;
            if (!TryCreate(info, stops, out line)) return;

            Tour t = new Tour { Line = line, LastSweep = sweep, Length = length, Goodness = goodness };
            SetAppeal(ref t, stops);
            _byTip[key] = t;
            Log.DebugLog("walking tour created: line " + line + ", len " + Mathf.RoundToInt(length) +
                         "m, " + stops.Count + " stops, goodness " + goodness.ToString("F2") +
                         ", total tours " + _byTip.Count);
        }

        // Two appeal points spread along the route (start and middle) so cims are drawn to walk it.
        private static void SetAppeal(ref Tour t, List<Vector3> stops)
        {
            t.AppealA = stops[0];
            t.AppealB = stops[stops.Count / 2];
        }

        private static bool FindWeakest(out ushort key, out float goodness)
        {
            key = 0;
            goodness = float.MaxValue;
            foreach (KeyValuePair<ushort, Tour> kv in _byTip)
                if (kv.Value.Goodness < goodness) { goodness = kv.Value.Goodness; key = kv.Key; }
            return key != 0;
        }

        // Rebuild a line's stops in place (keeps the line id/colour): drop all stops, add the new
        // ones, re-close, re-commit. Used when the underlying path changed.
        private static bool RebuildStops(ushort line, List<Vector3> stops)
        {
            if (stops.Count < 2 || !Singleton<TransportManager>.exists) return false;
            TransportManager tm = Singleton<TransportManager>.instance;
            if ((tm.m_lines.m_buffer[line].m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None)
                return false;

            int guard = 0;
            while (tm.m_lines.m_buffer[line].m_stops != 0 && guard++ < 512)
                if (!tm.m_lines.m_buffer[line].RemoveStop(line, 0)) break;

            int placed = 0;
            for (int i = 0; i < stops.Count; i++)
            {
                if (!tm.m_lines.m_buffer[line].CanAddStop(line, placed, stops[i])) continue;
                if (tm.m_lines.m_buffer[line].AddStop(line, placed, stops[i], false)) placed++;
            }
            if (placed < 2) return false;

            ushort first = tm.m_lines.m_buffer[line].m_stops;
            if (first != 0)
            {
                Vector3 firstPos = Singleton<NetManager>.instance.m_nodes.m_buffer[first].m_position;
                tm.m_lines.m_buffer[line].AddStop(line, -1, firstPos, false);
            }
            _finalizeFrames = 480;
            return true;
        }

        // Inject "appeal" (Attractiveness) at the two points of each tour, every tick (sustained
        // like the leisure value). Only the few tour points get it, so it is a local draw, not a
        // map-wide bloom. amount = SmallParkRate x AttractivenessShare.
        internal static void InjectAppeal(Settings s)
        {
            if (_byTip.Count == 0 || s.AttractivenessShare <= 0f) return;
            if (!Singleton<TransportManager>.exists || !Singleton<NetManager>.exists) return;
            int amount = Mathf.RoundToInt(s.SmallParkRate * s.AttractivenessShare);
            if (amount < 1) return;
            ImmaterialResourceManager irm = Singleton<ImmaterialResourceManager>.instance;
            foreach (Tour t in _byTip.Values)
            {
                irm.AddResource(ImmaterialResourceManager.Resource.Attractiveness, amount, t.AppealA, s.AppealRadius);
                irm.AddResource(ImmaterialResourceManager.Resource.Attractiveness, amount, t.AppealB, s.AppealRadius);
            }
        }

        // Aggregate usage across our tour lines: how many completed and the resident/tourist split
        // of their passengers. The decisive check for "are residents (not only tourists) using them".
        internal static void LogUsage()
        {
            if (!Log.DebugEnabled || _byTip.Count == 0 || !Singleton<TransportManager>.exists) return;
            TransportLine[] buf = Singleton<TransportManager>.instance.m_lines.m_buffer;
            int complete = 0;
            // m_averageCount is the smoothed value the game's line panel shows; m_finalCount reads
            // ~0 between weekly cycles, which earlier made working tours look unused.
            uint residents = 0, tourists = 0;
            foreach (Tour t in _byTip.Values)
            {
                if ((buf[t.Line].m_flags & TransportLine.Flags.Complete) != TransportLine.Flags.None)
                    complete++;
                residents += buf[t.Line].m_passengers.m_residentPassengers.m_averageCount;
                tourists += buf[t.Line].m_passengers.m_touristPassengers.m_averageCount;
            }
            Log.DebugLog("tour usage: " + _byTip.Count + " lines, " + complete + " complete; " +
                         "avg passengers residents=" + residents + " tourists=" + tourists);
        }

        private static bool TryCreate(TransportInfo info, List<Vector3> stops, out ushort line)
        {
            line = 0;
            if (stops.Count < 2) return false;

            TransportManager tm = Singleton<TransportManager>.instance;
            Randomizer r = Singleton<SimulationManager>.instance.m_randomizer;
            if (!tm.CreateLine(out line, ref r, info, true)) return false;

            // Fund the line so it actually spawns walking groups (budget 0 = no groups). Higher
            // budget = more/larger groups = more cims out walking (tunable).
            int budget = Settings.Instance.TourBudget;
            if (budget < 1) budget = 1;
            if (budget > 1000) budget = 1000;
            tm.m_lines.m_buffer[line].m_budget = (ushort)budget;

            // Distinct brown so the player can tell our auto-tours from their own lines.
            tm.m_lines.m_buffer[line].m_color = TourColor;
            tm.m_lines.m_buffer[line].m_flags |= TransportLine.Flags.CustomColor;

            int placed = 0;
            for (int i = 0; i < stops.Count; i++)
            {
                if (!tm.m_lines.m_buffer[line].CanAddStop(line, placed, stops[i])) continue;
                if (tm.m_lines.m_buffer[line].AddStop(line, placed, stops[i], false)) placed++;
            }
            if (placed < 2)
            {
                tm.ReleaseLine(line);
                line = 0;
                Log.DebugLog("tour create failed: only " + placed + " of " + stops.Count +
                             " stops were placeable on the line");
                return false;
            }

            // Close the ring the way the transport tool does (append AddStop on the first stop).
            ushort first = tm.m_lines.m_buffer[line].m_stops;
            if (first != 0)
            {
                Vector3 firstPos = Singleton<NetManager>.instance.m_nodes.m_buffer[first].m_position;
                tm.m_lines.m_buffer[line].AddStop(line, -1, firstPos, false);
            }

            _lineIds.Add(line);
            _finalizeFrames = 480; // ~8 s of UpdateLinesNow to commit stop-to-stop paths
            return true;
        }

        // Per-frame (even while paused): commit freshly built line paths. Cheap no-op when idle.
        internal static void Tick()
        {
            if (_finalizeFrames <= 0 || !Singleton<TransportManager>.exists) return;
            _finalizeFrames--;
            TransportManager tm = Singleton<TransportManager>.instance;
            tm.UpdateLinesNow();

            // When the commit window closes, report each line's completion + drawn length, and drop
            // any that formed no route (length 0 - the "far sprawling path" case where the stops are
            // too far apart to path between).
            if (_finalizeFrames == 0)
            {
                TransportLine[] buf = tm.m_lines.m_buffer;
                _evict.Clear();
                foreach (KeyValuePair<ushort, Tour> kv in _byTip)
                {
                    ushort lineId = kv.Value.Line;
                    bool complete = (buf[lineId].m_flags & TransportLine.Flags.Complete) != TransportLine.Flags.None;
                    int len = Mathf.RoundToInt(buf[lineId].m_totalLength);
                    if (Log.DebugEnabled)
                        Log.DebugLog("tour line " + lineId + ": complete=" + complete + " length=" + len + "m");
                    if (len <= 0) _evict.Add(kv.Key);
                }
                for (int i = 0; i < _evict.Count; i++)
                {
                    Release(_byTip[_evict[i]].Line);
                    _byTip.Remove(_evict[i]);
                }
                if (Log.DebugEnabled && _evict.Count > 0)
                    Log.DebugLog("dropped " + _evict.Count + " broken (0-length) tour line(s)");
            }
        }

        // Drop tours whose path wasn't re-confirmed in the sweep just completed.
        internal static void EvictStale(int currentSweep)
        {
            _evict.Clear();
            foreach (KeyValuePair<ushort, Tour> kv in _byTip)
                if (kv.Value.LastSweep < currentSweep) _evict.Add(kv.Key);
            for (int i = 0; i < _evict.Count; i++)
            {
                Release(_byTip[_evict[i]].Line);
                _byTip.Remove(_evict[i]);
            }
            if (Log.DebugEnabled && _evict.Count > 0)
                Log.DebugLog("walking tours evicted " + _evict.Count + " (gone paths), " +
                             _byTip.Count + " left");
        }

        internal static void ReleaseAll()
        {
            foreach (Tour t in _byTip.Values) Release(t.Line);
            int n = _byTip.Count;
            _byTip.Clear();
            _lineIds.Clear();
            _finalizeFrames = 0;
            _lastRegen = System.DateTime.MinValue;
            if (n > 0) Log.Info("Released " + n + " walking-tour line(s)");
        }

        private static void Release(ushort line)
        {
            _lineIds.Remove(line);
            if (line == 0 || !Singleton<TransportManager>.exists) return;
            try { Singleton<TransportManager>.instance.ReleaseLine(line); }
            catch { }
        }

        // ── Save exclusion (called by the Harmony patch bracketing Data.Serialize) ────────────
        internal static void HideForSave()
        {
            _stashed.Clear();
            if (_byTip.Count == 0 || !Singleton<TransportManager>.exists) return;
            TransportLine[] buf = Singleton<TransportManager>.instance.m_lines.m_buffer;
            foreach (Tour t in _byTip.Values)
            {
                _stashed.Add(new KeyValuePair<ushort, TransportLine.Flags>(t.Line, buf[t.Line].m_flags));
                buf[t.Line].m_flags = TransportLine.Flags.None;
            }
        }

        internal static void RestoreAfterSave()
        {
            if (_stashed.Count == 0 || !Singleton<TransportManager>.exists) return;
            TransportLine[] buf = Singleton<TransportManager>.instance.m_lines.m_buffer;
            for (int i = 0; i < _stashed.Count; i++)
                buf[_stashed[i].Key].m_flags = _stashed[i].Value;
            _stashed.Clear();
        }

        private static TransportInfo GetInfo()
        {
            if (_infoSearched) return _info;
            _infoSearched = true;

            // Pick the GENUINE walking-tour prefab: Pedestrian transport AND the PublicTransportTours
            // sub-service. Line-manager mods (e.g. Transport Lines Manager) classify every line by
            // {transportType, subService, vehicleType, level}; a Pedestrian line whose sub-service is
            // NOT PublicTransportTours has no matching descriptor there, and their patched
            // TransportLine.SimulationStep then throws a NullReferenceException every tick. We
            // therefore use ONLY the real tour prefab, so our lines are indistinguishable from vanilla
            // Parklife walking tours. If it is absent (it shouldn't be when Parklife is owned), Half 2
            // stays off rather than create a line that clashes with those mods.
            int n = PrefabCollection<TransportInfo>.LoadedCount();
            for (uint i = 0; i < n; i++)
            {
                TransportInfo info = PrefabCollection<TransportInfo>.GetLoaded(i);
                if (info != null &&
                    info.m_transportType == TransportInfo.TransportType.Pedestrian &&
                    info.m_class != null &&
                    info.m_class.m_subService == ItemClass.SubService.PublicTransportTours)
                {
                    _info = info;
                    break;
                }
            }

            if (_info == null)
                Log.Warning("Walking-tour prefab (Pedestrian + PublicTransportTours) not found - " +
                            "Half 2 disabled.");
            else
                Log.Info("Walking-tour TransportInfo: " + _info.name + " (subService " +
                         _info.m_class.m_subService + ", level " + _info.GetClassLevel() + ")");
            return _info;
        }
    }
}
