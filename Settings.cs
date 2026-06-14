using System;
using System.IO;
using System.Xml.Serialization;
using ColossalFramework.IO;
using UnityEngine;

namespace TakeAWalk
{
    [XmlRoot("TakeAWalkSettings")]
    public class Settings
    {
        private static Settings _instance;
        public static Settings Instance => _instance ?? (_instance = Load());

        // ── General ──────────────────────────────────────────────────────────
        public bool Enabled = true;

        // ── Eligibility gates ────────────────────────────────────────────────
        // Ground pollution (0-255) above which a path stops counting as "scenic".
        public int PollutionThreshold = 50;
        // Water pollution (0-255) of nearby water above which a path stops counting as "scenic" -
        // a walk along a filthy, contaminated shore is not pleasant. Sampled around the path point;
        // dry inland paths (no water nearby) are unaffected.
        public int WaterPollutionThreshold = 40;
        // How strongly nearby water pollution eats into the scenic score (per pollution unit), for
        // water below the hard threshold. Kept high on purpose - polluted water is a strong deterrent.
        public float WaterPollutionSensitivity = 0.5f;
        // How strongly local noise pollution eats into the scenic score (per noise unit).
        public float NoiseSensitivity = 0.15f;
        // Fraction of a nearby landmark's bonus that is allowed to cancel the noise
        // penalty - the "you don't mind the noise next to the Eiffel tower" effect.
        public float LandmarkNoiseForgiveness = 1.0f;

        // ── Point-of-interest weighting ──────────────────────────────────────
        // Radius (metres) around a path sample searched for trees/props/landmarks.
        // "2 grids ahead": the tree grid cell is 32 m, so ~2 cells.
        public float FeatureRadius = 64f;
        public float TreeWeight = 1.0f;       // score per nearby tree
        public float PropWeight = 1.5f;       // score per nearby prop
        public float LandmarkBonus = 25f;     // flat bonus when a park/landmark is nearby
        public int MaxTreesCounted = 20;      // cap so a forest doesn't run away
        public int MaxPropsCounted = 20;

        // ── Quality scoring (feeds the quality multiplier) ───────────────────
        public float MaxScore = 100f;         // hard clamp on a segment's quality score

        // ── Length + decoration leisure value ────────────────────────────────
        // Entertainment rate = SmallParkRate · clamp(lengthFactor + decorationFactor, 0, Max).
        // Re-injected EVERY tick from the cache so the grid is sustained like a real park.
        // The two contributions are ADDITIVE so a path earns park-like value two ways:
        //   • length  - a long path is a walkable zone (lengthFactor = length / LengthForFullValue)
        //   • decoration - trees/props/landmark let you "build a park" out of a path
        //     (decorationFactor = quality / DecorationForFullValue; quality = trees·TreeWeight
        //      + props·PropWeight + landmark). A small bare path earns ~nothing (skipped).
        // SmallParkRate ≈ a vanilla small park's per-step Entertainment accumulation.
        public float SmallParkRate = 12f;
        // Contiguous length (m) worth one small park on its own. 10 grid units × 8 m = 80 m.
        public float LengthForFullValue = 80f;
        // Decoration quality worth one small park on its own (so a richly decorated short path
        // matches a real park). Lower = decoration counts for more.
        public float DecorationForFullValue = 40f;
        public float MaxTotalMultiplier = 3f;  // overall cap (~3× a small park)
        public float InjectRadius = 48f;      // AddResource spread radius (metres)
        public bool InjectSightseeing = true; // also add a little Sightseeing/TourCoverage
        // Walking is healthy: also add a small Health boost = HealthShare x the leisure rate.
        // Kept tiny on purpose (a fraction of the leisure value). 0 disables it.
        public float HealthShare = 0.25f;
        // "Appeal": Attractiveness added at ONE point per tour (its start), amount =
        // AttractivenessShare x SmallParkRate. Attractiveness is the tourism-appeal stat that draws
        // visitors/tourists - the cims who take walking tours. Injected only at the few tour spots
        // (not along every path), so it is a small local draw, not a network-wide bloom. 0 disables.
        // Injected every tick at the tour points (sustained like the leisure value).
        // In-game this is shown as a 0-50 strength (1 = 0.01, 50 = 0.5): ~0.1 reads as a normal
        // local draw, ~0.5 as a strong one. Stored here as the raw fractional share.
        public float AttractivenessShare = 0.1f;
        // Spread radius (metres) for the appeal injection. 0 = the tightest the engine allows: the
        // immaterial-resource grid has a ~38 m minimum cell, so appeal can't be made any more local.
        public float AppealRadius = 0f;

        // ── Performance ──────────────────────────────────────────────────────
        // Net segments scanned per simulation tick (round-robin over the buffer).
        public int SegmentsPerTick = 128;
        // Bounded BFS cap when measuring a contiguous path's length / building a tour route.
        // Higher = long paths made of many short segments are measured fully (not truncated),
        // at some CPU cost. A dense/curvy loop can use hundreds of segments.
        public int MaxPathSegments = 512;

        // ── Half 2 - drawing cims onto dead-end scenic paths ─────────────────
        // One invisible, save-excluded attraction building is placed at the far tip of each
        // dead-end scenic path so cims walk the whole path to it, pause briefly, and leave.
        // See v2-redesign.md and cs1 memory save_serialization.md.
        public bool EnableWalkingTours = true;
        // Max walking-tour lines alive at once. Kept low on purpose - the game shares a 255-line
        // budget across ALL public transport, so we run a handful of rotating tours, not one per
        // path. The best paths are likelier to be chosen, and the set re-rolls weekly.
        public int MaxTours = 5;
        // Only paths at least this long (metres) get a walking tour - a stub isn't a walk.
        public float TourMinLength = 96f;
        // A tour is only created if housing is within this radius (metres) of the path entrance;
        // groups come from the population, so a path out in the wilderness would draw nobody.
        public float TourHousingRadius = 200f;
        // Max stops laid along a tour route. More = the route follows the path/loop more fully.
        public int MaxStops = 8;
        // Per-line budget (%). Higher = the game runs more/larger walking groups on each tour, so
        // more cims are out walking. 100 = the normal line budget.
        public int TourBudget = 150;

        private static string SettingsPath =>
            Path.Combine(DataLocation.localApplicationData, "TakeAWalk.xml");

        private static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    using (StreamReader r = new StreamReader(SettingsPath))
                        return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(r);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return new Settings();
        }

        public static void Save()
        {
            try
            {
                using (StreamWriter w = new StreamWriter(SettingsPath))
                    new XmlSerializer(typeof(Settings)).Serialize(w, Instance);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
