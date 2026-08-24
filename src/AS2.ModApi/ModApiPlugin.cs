using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AS2.ModApi
{
    /// <summary>Loads the shared API and applies its patches</summary>
    // Mods depend on this with [BepInDependency(ModApiPlugin.Id)], then use AS2Events for the menus
    // and the Lua states, and AS2GameEvents for what happens during a ride
    // Only AS2Events needs anything applied here
    // AS2GameEvents has no patch behind it at all, and it subscribes lazily on the first +=, so a
    // player with no mod wanting a ride event has no listener for one
    [BepInPlugin(Id, Name, Version)]
    public sealed class ModApiPlugin : BaseUnityPlugin
    {
        public const string Id = "as2.modapi";
        public const string Name = "Audiosurf 2 Mod API";
        public const string Version = "0.2.1";

        /// <summary>The log source, so the rest of the assembly logs without threading a reference</summary>
        // BepInEx tags each plugin's source, so these lines are attributed to the API and not to
        // whichever mod triggered them
        // Built here rather than assigned from Logger in Awake, because every public type in this
        // assembly logs through it and they are all reachable before Awake runs
        // A plugin calling AS2ModMenu.Register or AS2Paths.DataFile without declaring
        // [BepInDependency(ModApiPlugin.Id)] loads in whatever order the chainloader picked, and
        // the attribute is a convention this API cannot enforce
        // A null here answers that mistake with a NullReferenceException from inside the framework,
        // which is a bad way to learn about a missing attribute
        internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(Name);

        private Harmony _harmony;

        private void Awake()
        {
            try
            {
                _harmony = new Harmony(Id);
                Patches.ApplyAll(_harmony);
                Log.LogInfo("Mod API ready. Game root: " + TargetResolver.GameRoot);
            }
            catch (Exception e)
            {
                // A broken API must not stop the game or the other plugins from loading.
                Log.LogError("Mod API failed to initialise; events will not fire. " + e);
            }
        }

        /// <summary>
        /// The API owns the single "Mod Menu" button in the game's settings dialog and the hub
        /// behind it, so that mods do not each add a button and overflow the row.
        /// </summary>
        private void OnGUI()
        {
            try
            {
                AS2Ui.EnsureStyles();
                GUI.depth = -900;
                AS2ModMenu.Draw();
            }
            catch (Exception e)
            {
                Log.LogError("Mod Menu drawing failed; closing it. " + e);
                AS2ModMenu.Close();
            }
        }

        /// <summary>
        /// UnpatchSelf removes only the patches made under this Harmony id, despite the name
        /// reading like it might do more. That matters here: the community patch and other mods
        /// have their own patches on the same methods, and this must never strip them.
        /// </summary>
        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); }
            catch (Exception e) { Log.LogWarning("Could not unpatch cleanly: " + e.Message); }
        }
    }

}
