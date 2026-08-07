using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AS2.ModApi
{
    /// <summary>
    /// Loads the shared API and applies its patches. Mods depend on this with
    /// [BepInDependency(ModApiPlugin.Id)] and then talk only to <see cref="AS2Events"/>.
    /// </summary>
    [BepInPlugin(Id, Name, Version)]
    public sealed class ModApiPlugin : BaseUnityPlugin
    {
        public const string Id = "as2.modapi";
        public const string Name = "Audiosurf 2 Mod API";
        public const string Version = "0.1.0";

        /// <summary>
        /// Exposed statically so the rest of the assembly can log without threading a reference
        /// through every call. BepInEx gives each plugin its own tagged source, so these lines are
        /// attributed to the API rather than to whichever mod happened to trigger them.
        ///
        /// Built here rather than assigned from Logger in Awake, because every public type in this
        /// assembly logs through it and they are all reachable before Awake runs. A plugin that
        /// calls AS2ModMenu.Register or AS2Paths.DataFile without declaring
        /// [BepInDependency(ModApiPlugin.Id)] loads in whatever order the chainloader picked, and
        /// the dependency attribute is a convention this API cannot enforce. A null here would
        /// answer that mistake with a NullReferenceException thrown from inside the framework,
        /// which is a bad way to learn about a missing attribute.
        /// </summary>
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
