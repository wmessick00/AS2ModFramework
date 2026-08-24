using System;
using System.Reflection;
using HarmonyLib;
using LuaInterface;

namespace AS2.ModApi
{
    /// <summary>The Harmony patches behind <see cref="AS2Events"/></summary>
    // Every target resolves through AccessTools by name and is patched on its own, not with
    // [HarmonyPatch] attributes and PatchAll
    // The community patch ships a modified Assembly-CSharp and may rename a method. Attribute
    // patching throws during PatchAll and takes the whole API down with it
    // This way a moved member costs exactly one event, logs a warning naming it, and leaves the
    // rest working
    // Every signature here was read off a live runtime dump, not guessed. See AS2.Probe
    internal static class Patches
    {
        internal static void ApplyAll(Harmony harmony)
        {
            // Both selector screens are the same shape in the game's code (they look copy-pasted:
            // identical OnEnable/OnDisable/OnListItemSelected/hasSelectedADesign members), so one
            // helper wires up both.
            WireSelector(harmony, "RingDesignManager", SelectorKind.Skin);
            WireSelector(harmony, "ModeSelect", SelectorKind.Mode);

            WireLuaFactory(harmony);
            WireSettingsDialog(harmony);
        }

        /// <summary>
        /// The game's own settings dialog (`Settings : Menu`). Mods that want an entry point inside
        /// it rather than floating over the game need to know when it is up.
        /// </summary>
        private static void WireSettingsDialog(Harmony harmony)
        {
            Type t = AccessTools.TypeByName("Settings");
            if (t == null) { ModApiPlugin.Log.LogWarning("Settings type not found; settings dialog events are unavailable."); return; }

            Patch(harmony, t, "OnEnable", Type.EmptyTypes, nameof(SettingsDialogEnabled));
            Patch(harmony, t, "OnDisable", Type.EmptyTypes, nameof(SettingsDialogDisabled));
        }

        /// <summary>
        /// OnEnable/OnDisable are the selector's real lifecycle: the prefabs are instantiated by
        /// SkinSelectorLoader / ModeSelectorLoader, so their enable state is what "the screen is up"
        /// actually means. This replaces polling FindObjectOfType every 15 frames.
        /// </summary>
        private static void WireSelector(Harmony harmony, string typeName, SelectorKind kind)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null) { ModApiPlugin.Log.LogWarning("Type " + typeName + " not found; " + kind + " selector events are unavailable."); return; }

            Patch(harmony, t, "OnEnable", Type.EmptyTypes,
                  kind == SelectorKind.Skin ? nameof(SkinSelectorEnabled) : nameof(ModeSelectorEnabled));

            Patch(harmony, t, "OnDisable", Type.EmptyTypes,
                  kind == SelectorKind.Skin ? nameof(SkinSelectorDisabled) : nameof(ModeSelectorDisabled));

            // OnListItemSelected(int index, GameObject tile) is the highlight-changed callback.
            Patch(harmony, t, "OnListItemSelected", null,
                  kind == SelectorKind.Skin ? nameof(SkinSelectionChanged) : nameof(ModeSelectionChanged));
        }

        /// <summary>Patches LuaSandbox.NewLua, the one factory every Lua state is born from</summary>
        // A runtime probe confirmed it is called with exactly "Skin" and "Mod"
        // Patching it lets AS2Events.LuaStateCreated replace both of the game's Messenger
        // broadcasts and the reflection into LuaMods' private 'lua' field
        // Being a postfix is load-bearing beyond needing __result: NewLua sandboxes the state
        // before returning (SecureLuaFunctions, Sandboxify, LoadSafeTypes), so running after it is
        // what hands subscribers a sandboxed state rather than a raw interpreter
        // Do not move this earlier in the call, and do not patch Sandboxify
        private static void WireLuaFactory(Harmony harmony)
        {
            Type sandbox = AccessTools.TypeByName("LuaSandbox");
            if (sandbox == null) { ModApiPlugin.Log.LogWarning("LuaSandbox not found; LuaStateCreated will never fire."); return; }

            Patch(harmony, sandbox, "NewLua", null, nameof(NewLuaPostfix));
        }

        private static void Patch(Harmony harmony, Type target, string method, Type[] parameters, string postfixName)
        {
            try
            {
                MethodInfo original = parameters == null
                    ? AccessTools.Method(target, method)
                    : AccessTools.Method(target, method, parameters);

                if (original == null)
                {
                    ModApiPlugin.Log.LogWarning(target.Name + "." + method + " not found in this build; that event is unavailable.");
                    return;
                }

                var postfix = new HarmonyMethod(AccessTools.Method(typeof(Patches), postfixName));
                harmony.Patch(original, null, postfix);
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogError("Could not patch " + target.Name + "." + method + ": " + e);
            }
        }

        // ---- Postfixes -------------------------------------------------------------------------
        //
        // Harmony resolves these by name, so they must stay static and must not be renamed without
        // updating the nameof() references above (which the compiler will catch).

        private static void SkinSelectorEnabled() { AS2Events.RaiseSelectorOpened(SelectorKind.Skin); }
        private static void SkinSelectorDisabled() { AS2Events.RaiseSelectorClosed(SelectorKind.Skin); }
        private static void ModeSelectorEnabled() { AS2Events.RaiseSelectorOpened(SelectorKind.Mode); }
        private static void ModeSelectorDisabled() { AS2Events.RaiseSelectorClosed(SelectorKind.Mode); }

        private static void SkinSelectionChanged() { AS2Events.RaiseSelectionChanged(SelectorKind.Skin); }
        private static void ModeSelectionChanged() { AS2Events.RaiseSelectionChanged(SelectorKind.Mode); }

        private static void SettingsDialogEnabled() { AS2Events.RaiseSettingsDialog(true); }
        private static void SettingsDialogDisabled() { AS2Events.RaiseSettingsDialog(false); }

        /// <summary>
        /// __0 is the 'name' argument and __result the new state. Indexed injection is used rather
        /// than __args because BepInEx 5 ships HarmonyX 2.9.0, which predates __args.
        /// </summary>
        private static void NewLuaPostfix(string __0, Lua __result)
        {
            AS2Events.RaiseLuaStateCreated(__result, __0);
        }
    }
}
