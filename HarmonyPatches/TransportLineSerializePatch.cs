using HarmonyLib;

namespace TakeAWalk.HarmonyPatches
{
    // Keeps our transient walking-tour lines out of the save: hide them (flags -> None) just
    // before TransportManager serializes its lines, restore immediately after (Finalizer runs even
    // if the original throws). The save never contains them; the live game keeps them; nothing is
    // persisted and the lines are recreated each session from the current scenic paths.
    [HarmonyPatch(typeof(TransportManager.Data), "Serialize")]
    internal static class TransportLineSerializePatch
    {
        private static void Prefix()
        {
            WalkingTourManager.HideForSave();
        }

        private static void Finalizer()
        {
            WalkingTourManager.RestoreAfterSave();
        }
    }
}
