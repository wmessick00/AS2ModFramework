using System;
using System.Collections.Generic;

namespace BepInEx
{
    /// <summary>
    /// Stands in for BepInEx's own Paths, which the preloader fills in and which therefore has no
    /// value outside a running game. TargetResolver.GameRoot reads GameRootPath and nothing else
    /// from it, so faking that one property is the whole of what the production code needs.
    /// </summary>
    internal static class Paths
    {
        internal static string GameRootPath;
    }
}

namespace AS2.ModApi
{
    /// <summary>
    /// Stands in for the plugin type, purely so the code under test can reach a logger.
    ///
    /// The real ModApiPlugin derives from BepInEx's BaseUnityPlugin, so loading it would drag in
    /// UnityEngine; it is not in this project's Compile list for that reason. TargetResolver only
    /// ever calls Log.LogWarning, so that is all this provides.
    /// </summary>
    internal static class ModApiPlugin
    {
        internal static readonly TestLog Log = new TestLog();
    }

    /// <summary>
    /// Collects the warnings the code under test emits, so a test can assert that a rejection was
    /// reported rather than silently swallowed. Refusing a bad key without saying so would be a
    /// bug of its own here: the whole design leans on a specific log line naming what was refused.
    /// </summary>
    internal sealed class TestLog
    {
        internal readonly List<string> Warnings = new List<string>();

        internal void LogWarning(object message)
        {
            Warnings.Add(message == null ? "" : message.ToString());
        }

        internal void Clear() { Warnings.Clear(); }

        /// <summary>Whether any warning so far contains the given fragment, ignoring case.</summary>
        internal bool Mentions(string fragment)
        {
            foreach (string w in Warnings)
                if (w.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
