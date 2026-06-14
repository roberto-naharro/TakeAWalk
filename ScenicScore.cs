using ColossalFramework;
using UnityEngine;

namespace TakeAWalk
{
    // Result of scoring a path point: how scenic it is, and the nearest existing
    // building we can reuse as a leisure "anchor" (0 = none).
    internal struct ScenicResult
    {
        internal bool Eligible;   // passed the ground-pollution gate (a walkable path qualifies)
        internal float Score;     // quality 0..MaxScore (0 = a plain path; still gets base value)
        internal ushort AnchorBuilding;
        // Breakdown - populated for debug logging; cheap (already computed).
        internal int Trees;
        internal int Props;
        internal int Noise;
        internal int Water;   // worst nearby water pollution sampled (0 = no/clean water nearby)
    }

    // Computes how "scenic" a world position is, by sampling the game's own grids:
    // nearby trees/props/landmarks raise the score, ground pollution gates it out,
    // and noise pollution eats into it (partly forgiven by a strong nearby landmark).
    //
    // It also locates an existing visitor-capable building near the point. That
    // building - never one we create - is reused as the destination that draws cims
    // onto the path, so nothing is persisted to the save and nothing is orphaned if
    // the mod is removed.
    internal static class ScenicScore
    {
        // Tree grid: 540 cells over the 17 280 m world → 32 m per cell.
        private const int TreeRes = 540;
        private const float TreeCell = 32f;
        // Prop & building grids: 270 cells → 64 m per cell.
        private const int PropRes = 270;
        private const float PropCell = 64f;
        private const int BuildingRes = 270;
        private const float BuildingCell = 64f;

        internal static ScenicResult Evaluate(Vector3 pos)
        {
            Settings s = Settings.Instance;
            ScenicResult result = new ScenicResult();

            // Gate - only low ground-pollution areas qualify; a polluted path is not scenic.
            byte pollution;
            Singleton<NaturalResourceManager>.instance.CheckPollution(pos, out pollution);
            if (pollution > s.PollutionThreshold)
                return result;   // Eligible stays false → skipped

            // Gate - a path hugging filthy, contaminated water is not a pleasant stroll. Sample the
            // water pollution around the point; dry inland paths (no water nearby) read 0 and pass.
            int waterPollution = SampleWaterPollution(pos, s.FeatureRadius);
            if (waterPollution > s.WaterPollutionThreshold)
                return result;   // Eligible stays false → skipped

            // Past the gate any walkable path qualifies (Score may be 0 for a plain path; it
            // still earns the length-scaled base value). Quality lifts it toward the cap.
            result.Eligible = true;

            int trees = CountTrees(pos, s.FeatureRadius, s.MaxTreesCounted);
            int props = CountProps(pos, s.FeatureRadius, s.MaxPropsCounted);
            ushort anchor = FindAnchorBuilding(pos, s.FeatureRadius);
            result.Trees = trees;
            result.Props = props;
            result.AnchorBuilding = anchor;

            float landmarkBonus = anchor != 0 ? s.LandmarkBonus : 0f;
            float scenic = trees * s.TreeWeight + props * s.PropWeight + landmarkBonus;

            // Noise penalty, partly cancelled by a nearby landmark ("Eiffel-tower effect").
            int noise;
            Singleton<ImmaterialResourceManager>.instance.CheckLocalResource(
                ImmaterialResourceManager.Resource.NoisePollution, pos, out noise);
            result.Noise = noise;
            result.Water = waterPollution;
            float noisePenalty = noise * s.NoiseSensitivity - landmarkBonus * s.LandmarkNoiseForgiveness;
            if (noisePenalty < 0f)
                noisePenalty = 0f;

            // Polluted water below the hard gate still drags the score down (a strong deterrent).
            float waterPenalty = waterPollution * s.WaterPollutionSensitivity;

            float score = scenic - noisePenalty - waterPenalty;
            if (score < 0f) score = 0f;
            if (score > s.MaxScore) score = s.MaxScore;

            result.Score = score;
            return result;
        }

        private static int CountTrees(Vector3 pos, float radius, int cap)
        {
            TreeManager tm = Singleton<TreeManager>.instance;
            uint[] grid = tm.m_treeGrid;
            TreeInstance[] buffer = tm.m_trees.m_buffer;
            float r2 = radius * radius;
            int count = 0;

            int minX = Clamp((int)((pos.x - radius) / TreeCell + TreeRes / 2), TreeRes);
            int maxX = Clamp((int)((pos.x + radius) / TreeCell + TreeRes / 2), TreeRes);
            int minZ = Clamp((int)((pos.z - radius) / TreeCell + TreeRes / 2), TreeRes);
            int maxZ = Clamp((int)((pos.z + radius) / TreeCell + TreeRes / 2), TreeRes);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    uint id = grid[z * TreeRes + x];
                    int guard = 0;
                    while (id != 0u && guard++ < 32768)
                    {
                        if ((buffer[id].m_flags & (ushort)TreeInstance.Flags.Created) != 0 &&
                            (buffer[id].m_flags & (ushort)TreeInstance.Flags.Deleted) == 0)
                        {
                            if (SqrDistXZ(buffer[id].Position, pos) <= r2)
                            {
                                if (++count >= cap) return count;
                            }
                        }
                        id = buffer[id].m_nextGridTree;
                    }
                }
            }
            return count;
        }

        private static int CountProps(Vector3 pos, float radius, int cap)
        {
            PropManager pm = Singleton<PropManager>.instance;
            ushort[] grid = pm.m_propGrid;
            PropInstance[] buffer = pm.m_props.m_buffer;
            float r2 = radius * radius;
            int count = 0;

            int minX = Clamp((int)((pos.x - radius) / PropCell + PropRes / 2), PropRes);
            int maxX = Clamp((int)((pos.x + radius) / PropCell + PropRes / 2), PropRes);
            int minZ = Clamp((int)((pos.z - radius) / PropCell + PropRes / 2), PropRes);
            int maxZ = Clamp((int)((pos.z + radius) / PropCell + PropRes / 2), PropRes);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    ushort id = grid[z * PropRes + x];
                    int guard = 0;
                    while (id != 0 && guard++ < 65536)
                    {
                        if ((buffer[id].m_flags & (ushort)PropInstance.Flags.Created) != 0 &&
                            (buffer[id].m_flags & (ushort)PropInstance.Flags.Deleted) == 0)
                        {
                            if (SqrDistXZ(buffer[id].Position, pos) <= r2)
                            {
                                if (++count >= cap) return count;
                            }
                        }
                        id = buffer[id].m_nextGridProp;
                    }
                }
            }
            return count;
        }

        // Nearest existing building near pos that accepts leisure visitors (a park,
        // Parklife park building, monument, or leisure venue). Returns 0 if none.
        // We only ever reuse buildings that already exist in the save.
        private static ushort FindAnchorBuilding(Vector3 pos, float radius)
        {
            BuildingManager bm = Singleton<BuildingManager>.instance;
            ushort[] grid = bm.m_buildingGrid;
            Building[] buffer = bm.m_buildings.m_buffer;
            float r2 = radius * radius;
            float best = r2;
            ushort bestId = 0;

            int minX = Clamp((int)((pos.x - radius) / BuildingCell + BuildingRes / 2), BuildingRes);
            int maxX = Clamp((int)((pos.x + radius) / BuildingCell + BuildingRes / 2), BuildingRes);
            int minZ = Clamp((int)((pos.z - radius) / BuildingCell + BuildingRes / 2), BuildingRes);
            int maxZ = Clamp((int)((pos.z + radius) / BuildingCell + BuildingRes / 2), BuildingRes);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    ushort id = grid[z * BuildingRes + x];
                    int guard = 0;
                    while (id != 0 && guard++ < 49152)
                    {
                        if ((buffer[id].m_flags & Building.Flags.Created) != 0 &&
                            (buffer[id].m_flags & Building.Flags.Deleted) == 0 &&
                            AcceptsLeisureVisitors(buffer[id].Info))
                        {
                            float d = SqrDistXZ(buffer[id].m_position, pos);
                            if (d <= best)
                            {
                                best = d;
                                bestId = id;
                            }
                        }
                        id = buffer[id].m_nextGridBuilding;
                    }
                }
            }
            return bestId;
        }

        private static bool AcceptsLeisureVisitors(BuildingInfo info)
        {
            if (info == null || info.m_class == null) return false;
            ItemClass.Service svc = info.m_class.m_service;
            if (svc == ItemClass.Service.Beautification || svc == ItemClass.Service.Monument)
                return true;
            // Leisure / tourist commercial venues also draw entertainment visitors.
            if (svc == ItemClass.Service.Commercial)
            {
                ItemClass.SubService sub = info.m_class.m_subService;
                return sub == ItemClass.SubService.CommercialLeisure ||
                       sub == ItemClass.SubService.CommercialTourist;
            }
            return false;
        }

        // Worst water pollution (0-255) found in the water around pos. Samples the point and four
        // offsets at the search radius so a path running *alongside* water still detects it. Cells
        // with no water contribute nothing, so a dry inland path returns 0.
        private static int SampleWaterPollution(Vector3 pos, float radius)
        {
            WaterManager wm = Singleton<WaterManager>.instance;
            int worst = 0;
            worst = MaxWaterPollutionAt(wm, pos, worst);
            worst = MaxWaterPollutionAt(wm, new Vector3(pos.x + radius, pos.y, pos.z), worst);
            worst = MaxWaterPollutionAt(wm, new Vector3(pos.x - radius, pos.y, pos.z), worst);
            worst = MaxWaterPollutionAt(wm, new Vector3(pos.x, pos.y, pos.z + radius), worst);
            worst = MaxWaterPollutionAt(wm, new Vector3(pos.x, pos.y, pos.z - radius), worst);
            return worst;
        }

        private static int MaxWaterPollutionAt(WaterManager wm, Vector3 p, int worst)
        {
            bool water, sewage;
            byte pollution;
            wm.CheckWater(p, out water, out sewage, out pollution);
            if (water && pollution > worst) return pollution;
            return worst;
        }

        private static float SqrDistXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private static int Clamp(int v, int res)
        {
            if (v < 0) return 0;
            if (v > res - 1) return res - 1;
            return v;
        }
    }
}
