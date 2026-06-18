using ColossalFramework;
using HarmonyLib;
using TakeAWalk.Util;

namespace TakeAWalk.HarmonyPatches
{
    // Our auto-created walking tours must NOT charge the city upkeep - the player never placed them.
    // TransportLine.SimulationStep charges weekly maintenance as
    //   vehicleCount × TransportInfo.m_maintenanceCostPerVehicle
    //   + capacity × m_maintenanceCostPerPassenger
    // Those fields live on the SHARED Pedestrian TransportInfo, so we zero them only for the duration
    // of one of OUR line's own step and restore right after. Line steps run serially on the
    // simulation thread, so the swap can never leak into another line's charge. (Pattern taken from
    // the sibling SchoolBuses mod's LineMaintenancePatch.)
    [HarmonyPatch(typeof(TransportLine), "SimulationStep")]
    internal static class TourMaintenancePatch
    {
        internal struct Swap
        {
            public TransportInfo Info;
            public int PerVehicle;
            public float PerPassenger;
        }

        // Set once when a patch body first throws, so we log a managed stack instead of letting an
        // unhandled simulation-thread exception crash CS1 natively - without spamming every tick.
        private static bool _errorLogged;

        private static void Prefix(ushort lineID, out Swap __state)
        {
            __state = default(Swap);
            try
            {
                if (!WalkingTourManager.IsTourLine(lineID))
                    return;

                TransportInfo info = Singleton<TransportManager>.instance.m_lines.m_buffer[lineID].Info;
                if (info == null)
                    return;

                __state.Info = info;
                __state.PerVehicle = info.m_maintenanceCostPerVehicle;
                __state.PerPassenger = info.m_maintenanceCostPerPassenger;
                info.m_maintenanceCostPerVehicle = 0;
                info.m_maintenanceCostPerPassenger = 0f;
            }
            catch (System.Exception e)
            {
                if (!_errorLogged) { _errorLogged = true; Log.Error("TourMaintenancePatch.Prefix threw: " + e); }
            }
        }

        private static void Postfix(Swap __state)
        {
            try
            {
                if (__state.Info == null)
                    return;
                __state.Info.m_maintenanceCostPerVehicle = __state.PerVehicle;
                __state.Info.m_maintenanceCostPerPassenger = __state.PerPassenger;
            }
            catch (System.Exception e)
            {
                if (!_errorLogged) { _errorLogged = true; Log.Error("TourMaintenancePatch.Postfix threw: " + e); }
            }
        }
    }
}
