using System;
using LuaInterface;

namespace AS2.ModApi
{
    /// <summary>
    /// The event surface for Audiosurf 2 mods.
    ///
    /// The game broadcasts almost nothing about its own state, so before this existed every mod
    /// polled FindObjectOfType every few frames and reached into private fields by reflection to
    /// find out what the player had selected. These events are Harmony patches over the real
    /// methods (see Patches.cs), so they fire exactly when the thing happens and cost nothing when
    /// it does not.
    ///
    /// Subscribers are isolated: an exception thrown by one handler is logged and swallowed so it
    /// cannot take out the other subscribers or the game.
    /// </summary>
    public static class AS2Events
    {
        /// <summary>A skin or mode selector screen appeared.</summary>
        public static event Action<SelectorKind> SelectorOpened;

        /// <summary>A skin or mode selector screen went away.</summary>
        public static event Action<SelectorKind> SelectorClosed;

        /// <summary>
        /// The player highlighted a different entry. The string is the storage key
        /// (e.g. "skins/Rainbowdrive"), already normalised by <see cref="TargetResolver"/>.
        /// </summary>
        public static event Action<SelectorKind, string> SelectionChanged;

        /// <summary>
        /// A Lua state was just created, before the game has registered its own API into it and
        /// before the skin/mode script runs. This is the moment to add globals of your own.
        ///
        /// `kind` is the game's own label, verified at runtime to be "Skin" or "Mod". Every Lua
        /// state in the game comes through here, which is why this single event replaces both of
        /// the game's LuaSkinFunctionsRegistered / LuaModFunctionsRegistered messages and the
        /// reflection into the private LuaMods.lua field that reading the mode state used to need.
        ///
        /// <para>
        /// <b>The code that will run in this state is not yours and is not trusted.</b> Skins and
        /// modes are Steam Workshop downloads: a player subscribes, and someone else's Lua runs.
        /// The game treats them accordingly, and the state handed to you here has already been
        /// sandboxed -- LuaSandbox.NewLua calls SecureLuaFunctions, Sandboxify and LoadSafeTypes
        /// before returning. Between them those nil out `package`, `io`, `require`, `module`,
        /// `os.execute`, `os.exit`, `os.getenv`, `os.remove`, `os.rename`, and -- the two that
        /// matter most -- `luanet` and `load_assembly`, which are LuaInterface's bridge to the
        /// CLR. Reachable types are then narrowed to a whitelist of about forty.
        /// </para>
        ///
        /// <para>
        /// Every function you register lands on the far side of that whitelist. `RegisterFunction`
        /// does not consult it, so a helper added here is a hole punched straight through the
        /// sandbox, callable by any skin the player has ever subscribed to. So:
        /// </para>
        ///
        /// <list type="bullet">
        /// <item>Register nothing with file, network, process or reflection reach. A convenience
        /// wrapper over File.ReadAllText hands arbitrary file reads to untrusted content.</item>
        /// <item>Treat every argument as hostile -- wrong type, null, absurd length, a path that
        /// climbs. It is Lua: the signature guarantees you nothing.</item>
        /// <item>Keep what you expose to data your own mod owns, and keep it read-mostly.</item>
        /// </list>
        ///
        /// <para>
        /// The `Lua` handed to you belongs to the game and is disposed on
        /// LuaController.OnDisable; expect several states per song and make injection idempotent
        /// and cheap. Retaining the reference past this event means holding a live interpreter --
        /// including, on a "Mod" state, the scoring functions the game registers into it moments
        /// later. That is not a capability this API grants you (a BepInEx plugin has the whole
        /// process either way) but it is the shortest path to it, so reach for it deliberately.
        /// </para>
        /// </summary>
        public static event Action<Lua, string> LuaStateCreated;

        /// <summary>
        /// The game's own settings dialog opened or closed. Use this to put a mod's entry point
        /// inside the settings menu instead of floating it over the game;
        /// <see cref="AS2Ui.EntryButtonRect"/> is the spot the dialog leaves free for one.
        /// </summary>
        public static event Action<bool> SettingsDialogToggled;

        // ---- Current state -------------------------------------------------------------------

        /// <summary>Which selector is on screen, or null if neither is.</summary>
        public static SelectorKind? ActiveSelector { get; private set; }

        /// <summary>Storage key of the current selection on the active selector, or null.</summary>
        public static string ActiveKey { get; private set; }

        // ---- Raising (internal) --------------------------------------------------------------

        internal static void RaiseSelectorOpened(SelectorKind kind)
        {
            ActiveSelector = kind;
            ActiveKey = CurrentKey(kind);

            if (SelectorOpened == null) return;
            foreach (Action<SelectorKind> h in SelectorOpened.GetInvocationList())
                Safe("SelectorOpened", delegate { h(kind); });
        }

        internal static void RaiseSelectorClosed(SelectorKind kind)
        {
            if (ActiveSelector == kind) { ActiveSelector = null; ActiveKey = null; }

            if (SelectorClosed == null) return;
            foreach (Action<SelectorKind> h in SelectorClosed.GetInvocationList())
                Safe("SelectorClosed", delegate { h(kind); });
        }

        internal static void RaiseSelectionChanged(SelectorKind kind)
        {
            string key = CurrentKey(kind);
            ActiveSelector = kind;
            ActiveKey = key;

            if (SelectionChanged == null) return;
            foreach (Action<SelectorKind, string> h in SelectionChanged.GetInvocationList())
                Safe("SelectionChanged", delegate { h(kind, key); });
        }

        internal static void RaiseLuaStateCreated(Lua lua, string kind)
        {
            if (lua == null || LuaStateCreated == null) return;
            foreach (Action<Lua, string> h in LuaStateCreated.GetInvocationList())
                Safe("LuaStateCreated", delegate { h(lua, kind); });
        }

        internal static void RaiseSettingsDialog(bool open)
        {
            if (SettingsDialogToggled == null) return;
            foreach (Action<bool> h in SettingsDialogToggled.GetInvocationList())
                Safe("SettingsDialogToggled", delegate { h(open); });
        }

        /// <summary>
        /// Reads the game's own static selection state for one selector.
        ///
        /// CodeEditor.skinPath/modPath are absolute and only populated once a song has been set up,
        /// so they are the fallback rather than the primary source.
        /// </summary>
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

        /// <summary>Runs one subscriber. A mod that throws in a handler breaks only itself.</summary>
        private static void Safe(string name, Action body)
        {
            try { body(); }
            catch (Exception e) { ModApiPlugin.Log.LogError("A subscriber to " + name + " threw: " + e); }
        }
    }
}
