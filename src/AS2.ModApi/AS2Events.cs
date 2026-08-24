using System;
using LuaInterface;

namespace AS2.ModApi
{
    /// <summary>The event surface for Audiosurf 2 mods</summary>
    // Harmony patches over the real methods (see Patches.cs), so an event fires when the thing
    // happens and costs nothing when it does not
    // Subscribers are isolated: a handler that throws is logged and swallowed
    // Subscribe and unsubscribe from any thread. Handlers run on the thread that raised, which is
    // the game thread for all five
    // Each raise takes its handler list at the top, so a -= landing mid-raise still sees one more
    // call. A handler that unsubscribes itself has to tolerate that
    // What each event means and when it fires: see the AS2Events wiki page
    public static class AS2Events
    {
        /// <summary>A skin or mode selector screen appeared</summary>
        public static event Action<SelectorKind> SelectorOpened;

        /// <summary>A skin or mode selector screen went away</summary>
        public static event Action<SelectorKind> SelectorClosed;

        /// <summary>The player highlighted a different entry</summary>
        // The string is the storage key ("skins/Rainbowdrive"), normalised by TargetResolver
        public static event Action<SelectorKind, string> SelectionChanged;

        /// <summary>A Lua state was created, before the game's API is in it and before the script runs</summary>
        // The moment to add globals of your own
        // kind is the game's own label, verified at runtime to be "Skin" or "Mod"
        // Every Lua state comes through here, so this replaces both LuaSkinFunctionsRegistered and
        // LuaModFunctionsRegistered and the reflection into the private LuaMods.lua field
        //
        // The code that will run in this state is not yours and is not trusted -- skins and modes
        // are Steam Workshop downloads
        // NewLua has already sandboxed the state, but RegisterFunction does not consult that
        // whitelist, so anything registered here is reachable by every skin the player has
        // Register nothing with file, network, process or reflection reach, and treat every
        // argument as hostile
        // The Lua belongs to the game and is disposed on LuaController.OnDisable. Expect several
        // states per song, so make injection idempotent and cheap
        // The full sandbox argument: see the AS2Events and Lua States wiki pages
        public static event Action<Lua, string> LuaStateCreated;

        /// <summary>The game's own settings dialog opened or closed</summary>
        // Use it to put a mod's entry point inside the settings menu rather than floating it over
        // the game. <see cref="AS2Ui.EntryButtonRect"/> is the spot the dialog leaves free
        public static event Action<bool> SettingsDialogToggled;

        // ---- Current state -------------------------------------------------------------------

        /// <summary>Which selector is on screen, or null if neither is</summary>
        public static SelectorKind? ActiveSelector { get; private set; }

        /// <summary>Storage key of the current selection on the active selector, or null</summary>
        public static string ActiveKey { get; private set; }

        // Raising (internal)
        // ===========================================================================================
        // #26 -- every method here copies its event field to a local before it looks at it
        // A mod may call -= from a worker thread, and the last -= sets the field to null
        // Reading the field twice, once to null-check and once for GetInvocationList, lets that
        // removal land between the two, and GetInvocationList throws NullReferenceException
        // Safe does not cover it: Safe wraps the subscriber call, and this throws while building
        // the list of subscribers to call, leaving an uncaught throw in a postfix on the game thread
        // A local cannot change under the method. The cost is one more call to a handler that
        // unsubscribed a moment too late, which the class documents

        internal static void RaiseSelectorOpened(SelectorKind kind)
        {
            ActiveSelector = kind;
            ActiveKey = CurrentKey(kind);

            Action<SelectorKind> subscribers = SelectorOpened;
            if (subscribers == null) return;
            foreach (Action<SelectorKind> h in subscribers.GetInvocationList())
                Safe("SelectorOpened", delegate { h(kind); });
        }

        internal static void RaiseSelectorClosed(SelectorKind kind)
        {
            if (ActiveSelector == kind) { ActiveSelector = null; ActiveKey = null; }

            Action<SelectorKind> subscribers = SelectorClosed;
            if (subscribers == null) return;
            foreach (Action<SelectorKind> h in subscribers.GetInvocationList())
                Safe("SelectorClosed", delegate { h(kind); });
        }

        internal static void RaiseSelectionChanged(SelectorKind kind)
        {
            string key = CurrentKey(kind);
            ActiveSelector = kind;
            ActiveKey = key;

            Action<SelectorKind, string> subscribers = SelectionChanged;
            if (subscribers == null) return;
            foreach (Action<SelectorKind, string> h in subscribers.GetInvocationList())
                Safe("SelectionChanged", delegate { h(kind, key); });
        }

        internal static void RaiseLuaStateCreated(Lua lua, string kind)
        {
            Action<Lua, string> subscribers = LuaStateCreated;
            if (lua == null || subscribers == null) return;
            foreach (Action<Lua, string> h in subscribers.GetInvocationList())
                Safe("LuaStateCreated", delegate { h(lua, kind); });
        }

        internal static void RaiseSettingsDialog(bool open)
        {
            Action<bool> subscribers = SettingsDialogToggled;
            if (subscribers == null) return;
            foreach (Action<bool> h in subscribers.GetInvocationList())
                Safe("SettingsDialogToggled", delegate { h(open); });
        }

        /// <summary>Reads the game's own static selection state for one selector</summary>
        // CodeEditor.skinPath and modPath are absolute and only populated once a song is set up,
        // so they are the fallback rather than the primary source
        public static string CurrentKey(SelectorKind kind)
        {
            if (kind == SelectorKind.Skin)
            {
                try
                {
                    string rel = RingDesignManager.selectedSkinRelativePath;
                    if (!Str.IsBlank(rel)) return TargetResolver.Normalize(rel);
                }
                catch (Exception e) { ModApiPlugin.Log.LogWarning("Could not read the selected skin path: " + e.Message); }

                try { return TargetResolver.KeyForFolder(CodeEditor.skinPath); }
                catch { return null; }
            }

            try
            {
                string rel = ModeSelect.selectedModeScript.relativePath;
                if (!Str.IsBlank(rel)) return TargetResolver.Normalize("mods/" + rel);
            }
            catch (Exception e) { ModApiPlugin.Log.LogWarning("Could not read the selected mode path: " + e.Message); }

            try { return TargetResolver.KeyForFolder(CodeEditor.modPath); }
            catch { return null; }
        }

        /// <summary>Runs one subscriber. A mod that throws in a handler breaks only itself</summary>
        private static void Safe(string name, Action body)
        {
            try { body(); }
            catch (Exception e) { ModApiPlugin.Log.LogError("A subscriber to " + name + " threw: " + e); }
        }
    }
}
