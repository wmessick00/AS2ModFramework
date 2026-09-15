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

        /// <summary>
        /// The single source of truth for the version. tools\pack.ps1 greps this constant to name
        /// the release archives, and BepInEx prints it in LogOutput.log, so a downloaded file name
        /// and a user's log line always agree. Assembly version resources are off
        /// (GenerateAssemblyInfo in Directory.Build.props), so there is nothing else to keep in step.
        /// <para>
        /// Do not edit this by hand. tools\pack.ps1 -Publish decides the next version from what
        /// changed since the last release -- for this assembly, mostly from what happened to the
        /// public surface below -- writes it here, and commits it. Editing it yourself is still
        /// honoured, because a constant already ahead of the last published tag ships untouched.
        /// </para>
        /// </summary>
        public const string Version = "0.3.1";

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
        // GUI.depth is restored in a finally, and it is process-wide static state shared by every
        // OnGUI in the frame -- so the same rule AS2Ui.Fill keeps for GUI.color and AS2Ui.Toggle
        // keeps for GUI.matrix applies to it, for the same reason and with the same consequence
        // Left set, it pins the depth for every other mod's GUI.Window and IMGUI call in this frame
        // and in all of them after, because BepInEx loads this plugin once for the life of the
        // process. That reads as a rendering-order bug in whichever mod drew next
        // Set inside the try rather than before it: if EnsureStyles throws there is nothing to draw
        // and nothing to reorder, and the depth should not move for a frame that drew nothing
        private void OnGUI()
        {
            int previousDepth = GUI.depth;
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
            finally { GUI.depth = previousDepth; }
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
