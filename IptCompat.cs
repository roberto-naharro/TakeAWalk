using System;
using System.Collections.Generic;
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
    // installed. Resolved once and cached.
    //
    // The original Improved Public Transport 2 (and IPT3) have the same auto-show but no MarkKnown.
    // For those we fall back to adding the line id straight into the watcher's private
    // HashSet<ushort> _knownLines, which is exactly what MarkKnown does. We do it right after
    // CreateLine, before the line is Complete: the watcher only ever Adds lines that are Complete,
    // so it is not mutating the set for this line while we write to it.
    internal static class IptCompat
    {
        private static bool _resolved;
        private static FieldInfo _instanceField;   // LineWatcher.instance (public static)
        private static MethodInfo _markKnown;      // LineWatcher.MarkKnown(ushort)
        private static FieldInfo _knownLinesField; // fallback: LineWatcher._knownLines (private)

        // Tell IPT (if present) that this freshly created line is already known, so it does not
        // auto-show its info panel or overwrite our line with IPT's defaults. Safe no-op otherwise.
        internal static void NotifyLineCreated(ushort lineId)
        {
            try
            {
                if (!_resolved) Resolve();
                if (_instanceField == null) return;
                object watcher = _instanceField.GetValue(null);
                if (watcher == null) return;   // IPT loaded but its watcher not up yet

                if (_markKnown != null)
                {
                    _markKnown.Invoke(watcher, new object[] { lineId });
                    return;
                }

                HashSet<ushort> known = _knownLinesField != null
                    ? _knownLinesField.GetValue(watcher) as HashSet<ushort>
                    : null;
                if (known != null)
                    lock (known) known.Add(lineId);
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
            if (_markKnown == null)
                _knownLinesField = lw.GetField("_knownLines", BindingFlags.NonPublic | BindingFlags.Instance);

            if (_instanceField == null || (_markKnown == null && _knownLinesField == null))
                Log.Warning("IPT LineWatcher found but no way to mark lines as known; " +
                            "its auto-show cannot be suppressed for walking tours.");
            else
                Log.Info("IPT detected (" + lw.FullName + ", " +
                         (_markKnown != null ? "MarkKnown" : "known-lines fallback") +
                         "); walking tours will not trigger its auto-show line panel.");
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
