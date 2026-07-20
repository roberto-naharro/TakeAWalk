using System;
using System.Reflection;
using TakeAWalk.Util;

namespace TakeAWalk
{
    // Optional soft integration with Improved Public Transport (IPT Essentials).
    //
    // IPT's LineWatcher auto-opens the line info panel (and applies its own per-line defaults) for
    // every newly created transport line when its "Auto show line info" option is on. Our transient
    // walking-tour lines would therefore pop that panel each time one is created.
    //
    // IPT exposes LineWatcher.MarkKnown(lineId), which marks a line as already discovered so the
    // watcher skips both the panel and the defaults. We call it right after creating a tour line, via
    // reflection so IPT stays an OPTIONAL dependency: everything no-ops cleanly when IPT is not
    // installed, or on an older IPT that lacks the method. Resolved once and cached.
    internal static class IptCompat
    {
        private static bool _resolved;
        private static FieldInfo _instanceField;   // LineWatcher.instance (public static)
        private static MethodInfo _markKnown;      // LineWatcher.MarkKnown(ushort)

        // Tell IPT (if present) that this freshly created line is already known, so it does not
        // auto-show its info panel or overwrite our line with IPT's defaults. Safe no-op otherwise.
        internal static void NotifyLineCreated(ushort lineId)
        {
            try
            {
                if (!_resolved) Resolve();
                if (_markKnown == null || _instanceField == null) return;
                object watcher = _instanceField.GetValue(null);
                if (watcher == null) return;   // IPT loaded but its watcher not up yet
                _markKnown.Invoke(watcher, new object[] { lineId });
            }
            catch (Exception e)
            {
                // An optional-integration hiccup must never disturb tour creation.
                Log.DebugLog("IptCompat.NotifyLineCreated failed: " + e.Message);
            }
        }

        private static void Resolve()
        {
            _resolved = true;

            // Current IPT uses the ImprovedPublicTransport2 namespace; older builds used
            // ImprovedPublicTransport. Try both.
            Type lw = FindType("ImprovedPublicTransport2.LineWatcher") ??
                      FindType("ImprovedPublicTransport.LineWatcher");
            if (lw == null)
            {
                Log.Info("IPT not detected; walking tours will not need to suppress its auto-show.");
                return;
            }

            _instanceField = lw.GetField("instance", BindingFlags.Public | BindingFlags.Static);
            _markKnown = lw.GetMethod("MarkKnown", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(ushort) }, null);

            if (_instanceField == null || _markKnown == null)
                Log.Warning("IPT LineWatcher found but MarkKnown/instance is missing (older IPT?); " +
                            "its auto-show cannot be suppressed for walking tours.");
            else
                Log.Info("IPT detected; walking tours will suppress its auto-show line panel.");
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
