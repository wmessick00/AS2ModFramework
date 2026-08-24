using System;
using HarmonyLib;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>Cover for how <see cref="Patches"/> behaves when the game is the wrong shape</summary>
    // Patches.cs is the mod-loading mechanism, and its design is a bet about failure
    // Every target resolves by name through AccessTools and is patched one at a time, not with
    // [HarmonyPatch] attributes and PatchAll, so a renamed member costs one event and one warning
    // instead of taking the whole API down during PatchAll
    // That bet had no check behind it, and it is the hardest thing in the repo to verify by hand --
    // reproducing it means waiting for a community patch to rename something
    // The shims in Shims.cs resolve names against this assembly, so hiding one is a faithful
    // stand-in for a member that moved
    internal static class PatchChecks
    {
        /// <summary>
        /// Every patch ApplyAll should make against an intact game, by "Type.Member". Written out
        /// rather than counted, so removing a hook is a failure here and not a silently smaller
        /// number.
        /// </summary>
        private static readonly string[] Expected =
        {
            "RingDesignManager.OnEnable",
            "RingDesignManager.OnDisable",
            "RingDesignManager.OnListItemSelected",
            "ModeSelect.OnEnable",
            "ModeSelect.OnDisable",
            "ModeSelect.OnListItemSelected",
            "Settings.OnEnable",
            "Settings.OnDisable",
            "LuaSandbox.NewLua"
        };

        internal static void Run()
        {
            try
            {
                AnIntactGameGetsEveryHook();
                AMissingTypeCostsOneEventAndWarns();
                AMissingMethodCostsOneEventAndNamesIt();
                AFailingPatchDoesNotStopTheRest();
            }
            finally
            {
                AccessTools.Hidden.Clear();
            }
        }

        private static void AnIntactGameGetsEveryHook()
        {
            AccessTools.Hidden.Clear();
            var harmony = new Harmony("as2.modapi.tests");
            ModApiPlugin.Log.Clear();

            Patches.ApplyAll(harmony);

            foreach (string want in Expected)
                True("patches " + want, harmony.Applied.Contains(want));

            True("makes no patch it was not asked for (" + harmony.Applied.Count + ")",
                 harmony.Applied.Count == Expected.Length);

            False("and says nothing alarming about an intact game",
                  ModApiPlugin.Log.Mentions("not found") || ModApiPlugin.Log.Mentions("Could not patch"));
        }

        /// <summary>
        /// The case the by-name design exists for. A whole type gone should cost its own events and
        /// nothing else -- and it must say which one, because the alternative is a player reporting
        /// that "the settings button stopped working" with nothing in the log to act on.
        /// </summary>
        private static void AMissingTypeCostsOneEventAndWarns()
        {
            AccessTools.Hidden.Clear();
            AccessTools.Hidden.Add("Settings");

            var harmony = new Harmony("as2.modapi.tests");
            ModApiPlugin.Log.Clear();

            Patches.ApplyAll(harmony);

            False("a missing type takes its own hooks with it",
                  harmony.Applied.Contains("Settings.OnEnable") || harmony.Applied.Contains("Settings.OnDisable"));

            True("and leaves every other hook applied", harmony.Applied.Count == Expected.Length - 2);
            True("and names what went missing", ModApiPlugin.Log.Mentions("Settings"));
        }

        private static void AMissingMethodCostsOneEventAndNamesIt()
        {
            AccessTools.Hidden.Clear();
            AccessTools.Hidden.Add("RingDesignManager.OnListItemSelected");

            var harmony = new Harmony("as2.modapi.tests");
            ModApiPlugin.Log.Clear();

            Patches.ApplyAll(harmony);

            False("a renamed method costs only its own hook",
                  harmony.Applied.Contains("RingDesignManager.OnListItemSelected"));

            True("its neighbours on the same type still apply",
                 harmony.Applied.Contains("RingDesignManager.OnEnable")
                 && harmony.Applied.Contains("RingDesignManager.OnDisable"));

            True("and the warning names the member", ModApiPlugin.Log.Mentions("OnListItemSelected"));
        }

        /// <summary>
        /// A lookup that succeeds and a patch that then throws is a different path from a lookup
        /// that fails, and it is the one that would take the API down if it were not caught.
        /// </summary>
        private static void AFailingPatchDoesNotStopTheRest()
        {
            AccessTools.Hidden.Clear();
            var harmony = new Harmony("as2.modapi.tests");
            harmony.ThrowOn = "ModeSelect.OnEnable";
            ModApiPlugin.Log.Clear();

            Patches.ApplyAll(harmony);

            False("a patch that throws does not get recorded", harmony.Applied.Contains("ModeSelect.OnEnable"));
            True("but the ones after it still apply", harmony.Applied.Contains("LuaSandbox.NewLua"));
            True("and every other hook survives", harmony.Applied.Count == Expected.Length - 1);
            True("and the failure is reported", ModApiPlugin.Log.Mentions("Could not patch"));
        }
    }
}
