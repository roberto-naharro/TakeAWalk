using ICities;
using TakeAWalk.Util;

namespace TakeAWalk
{
    // Runs once when a city finishes loading, BEFORE the first simulation step. It purges stale
    // walking-tour transport lines that an older/buggy build persisted into the save. Those lines
    // would otherwise crash line-manager mods (Transport Lines Manager, IPT) on the first
    // TransportLine.SimulationStep and take the game down natively. See
    // WalkingTourManager.ReleaseStrayTourLines for the full rationale and the safe-to-remove test.
    //
    // LoadingExtensionBase classes are auto-discovered in the mod assembly, like the scanner's
    // ThreadingExtensionBase; nothing needs to register this.
    public class TourLineLoadCleanup : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);

            // Only in actual cities (not the map/asset editors).
            if (mode != LoadMode.LoadGame &&
                mode != LoadMode.NewGame &&
                mode != LoadMode.NewGameFromScenario)
                return;

            try
            {
                WalkingTourManager.ReleaseStrayTourLines();
            }
            catch (System.Exception e)
            {
                Log.Error("TourLineLoadCleanup.OnLevelLoaded threw: " + e);
            }
        }
    }
}
