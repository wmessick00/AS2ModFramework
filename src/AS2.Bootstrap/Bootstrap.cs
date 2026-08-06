using System;
using System.IO;
using System.Reflection;

namespace AS2.Bootstrap
{
    /// <summary>
    /// Doorstop entry point that hands control to BepInEx.
    ///
    /// Why this shim exists at all:
    ///
    /// Audiosurf 2's community patch ships UnityDoorstop 3.4.1, which resolves the method
    /// descriptor "*:Main" -- a method named Main in any class of the target assembly. BepInEx
    /// 5.4.23 ships Doorstop 4.5.0 and exposes only "Doorstop.Entrypoint:Start". The two
    /// conventions do not meet, so BepInEx cannot be dropped in as the doorstop target directly.
    ///
    /// The obvious fix -- run BepInEx's own installer -- is the wrong one here. It overwrites
    /// winhttp.dll and doorstop_config.ini, and both of those files live inside the community
    /// patch's update payload. The next patch update reverts them, BepInEx stops loading, and
    /// unlike the old ModSettings arrangement there is no code left running in-process to notice
    /// or repair it. So we bridge the conventions instead and never touch winhttp.dll.
    ///
    /// Everything below is individually try/caught. A mod loader that fails to start must not stop
    /// the game from starting, and must not stop the community patch from updating itself.
    /// </summary>
    public static class Bootstrap
    {
        private const string OurTarget = @"AS2ModLoader\AS2.Bootstrap.dll";
        private const string PreloaderRelative = @"BepInEx\core\BepInEx.Preloader.dll";

        /// <summary>
        /// The community patch's own preloader, which launches PatchUpdater.exe. We took the single
        /// doorstop slot away from it, so we invoke it ourselves and patch auto-update keeps working.
        ///
        /// This path is deliberately a constant rather than "whatever we displaced from the ini".
        /// Reading the displaced value back would happily resurrect a competing mod loader -- and
        /// the old ModSettings build in that slot re-asserts itself into doorstop_config.ini three
        /// times a session, which would fight us for the slot on every launch.
        /// </summary>
        private const string PatchPreloaderRelative = @"Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll";

        private static bool _booted;

        /// <summary>Entry point for the community patch's Doorstop 3.4.1 ("*:Main").</summary>
        public static void Main()
        {
            Boot();
        }

        internal static void Boot()
        {
            if (_booted) return;
            _booted = true;

            string root = GameRoot();

            try { BootLog.Init(Path.Combine(root, "AS2ModLoader")); }
            catch { /* logging is best-effort */ }

            BootLog.Info("AS2.Bootstrap starting. Game root: " + root);

            // The patch updater goes first, on purpose: if BepInEx is broken, the community patch
            // must still be able to update itself out of that state.
            try { ChainToPatchPreloader(root); }
            catch (Exception e) { BootLog.Error("Could not chain to the community patch preloader.", e); }

            try { EnsureDoorstopTarget(root); }
            catch (Exception e) { BootLog.Error("Could not re-assert the doorstop target.", e); }

            try { StartBepInEx(root); }
            catch (Exception e) { BootLog.Error("Could not start BepInEx; no plugins will load this run.", e); }
        }

        // ---- BepInEx ------------------------------------------------------------------------

        /// <summary>
        /// Loads BepInEx's preloader and calls Doorstop.Entrypoint.Start() on it.
        ///
        /// The one thing that must be fixed up first is DOORSTOP_INVOKE_DLL_PATH. BepInEx derives
        /// its entire directory layout from it:
        ///
        ///     preloaderPath = GetDirectoryName(GetFullPath(DOORSTOP_INVOKE_DLL_PATH))
        ///     bepinPath     = ParentDirectory(GetFullPath(DOORSTOP_INVOKE_DLL_PATH), 2)
        ///
        /// Doorstop set that variable to *this* assembly, so leaving it alone would convince BepInEx
        /// its root was AS2ModLoader\ and it would find no plugins. Pointing it at the preloader
        /// makes preloaderPath = BepInEx\core and bepinPath = BepInEx, which is what a stock install
        /// produces.
        ///
        /// The other three variables BepInEx reads -- DOORSTOP_PROCESS_PATH, DOORSTOP_MANAGED_FOLDER_DIR
        /// and DOORSTOP_DLL_SEARCH_DIRS -- describe the game, not the invoked assembly, and Doorstop
        /// 3.4.1 already sets all three correctly. They are left as-is.
        /// </summary>
        private static void StartBepInEx(string root)
        {
            string preloader = Path.Combine(root, PreloaderRelative.Replace('\\', Path.DirectorySeparatorChar));

            if (!File.Exists(preloader))
            {
                BootLog.Warn("BepInEx is not installed (no " + PreloaderRelative + "); nothing to start.");
                return;
            }

            Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", preloader);

            // Doorstop normally guarantees these. Fill them in defensively so a future doorstop
            // build that omits one cannot leave BepInEx computing paths from an empty string.
            if (IsBlank(Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH")))
                Environment.SetEnvironmentVariable("DOORSTOP_PROCESS_PATH", Path.Combine(root, "Audiosurf2.exe"));

            if (IsBlank(Environment.GetEnvironmentVariable("DOORSTOP_MANAGED_FOLDER_DIR")))
                Environment.SetEnvironmentVariable("DOORSTOP_MANAGED_FOLDER_DIR",
                    Path.Combine(root, Path.Combine("Audiosurf2_Data", "Managed")));

            Assembly asm = Assembly.LoadFrom(preloader);
            Type entry = asm.GetType("Doorstop.Entrypoint");
            if (entry == null)
            {
                BootLog.Error("BepInEx.Preloader.dll has no Doorstop.Entrypoint type; is this a BepInEx 5 build?", null);
                return;
            }

            MethodInfo start = entry.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (start == null)
            {
                BootLog.Error("Doorstop.Entrypoint has no static Start method.", null);
                return;
            }

            start.Invoke(null, null);
            BootLog.Info("Handed off to BepInEx. See BepInEx\\LogOutput.log from here on.");
        }

        // ---- Community patch ----------------------------------------------------------------

        private static void ChainToPatchPreloader(string root)
        {
            string path = Path.Combine(root, PatchPreloaderRelative.Replace('\\', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                BootLog.Info("No patch preloader at '" + PatchPreloaderRelative + "' (unpatched install?); continuing.");
                return;
            }

            Assembly asm = Assembly.LoadFrom(path);
            MethodInfo entry = asm.EntryPoint;
            if (entry == null)
            {
                foreach (Type t in asm.GetTypes())
                {
                    entry = t.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (entry != null) break;
                }
            }

            if (entry == null)
            {
                BootLog.Warn("Patch preloader has no Main; the community patch may not auto-update.");
                return;
            }

            // PatchUpdaterPreloader.Program.Main takes a string[]; tolerate a parameterless one too.
            object[] args = entry.GetParameters().Length == 0 ? null : new object[] { new string[0] };
            entry.Invoke(null, args);
            BootLog.Info("Chained into the community patch preloader.");
        }

        // ---- Doorstop config ------------------------------------------------------------------

        /// <summary>
        /// Re-asserts targetAssembly in doorstop_config.ini, rewriting only that one key and leaving
        /// every other line untouched.
        ///
        /// This is a fallback for the ini install, not the primary one. doorstop_config.ini is inside
        /// the community patch's payload, so a patch update reverts it and this repair only takes
        /// effect on the launch *after* the one that broke. The durable install is a Steam launch
        /// option:
        ///
        ///     --doorstop-target "&lt;game&gt;\AS2ModLoader\AS2.Bootstrap.dll"
        ///
        /// Doorstop 3.4.1 accepts that flag and command-line values win over the ini, so the patch
        /// can rewrite the file as often as it likes without breaking the install.
        ///
        /// Which is exactly why this does nothing when we were launched that way. Writing to the ini
        /// then would edit a file belonging to the community patch to no effect whatsoever, and a
        /// mod loader that silently rewrites another project's config is precisely what that project
        /// is entitled to object to. With the launch option, this install adds AS2ModLoader\ and
        /// BepInEx\ and modifies no game file at all.
        /// </summary>
        private static void EnsureDoorstopTarget(string root)
        {
            if (TargetedOnCommandLine())
            {
                BootLog.Info("Doorstop was targeted on the command line; leaving doorstop_config.ini untouched.");
                return;
            }

            string ini = Path.Combine(root, "doorstop_config.ini");
            if (!File.Exists(ini)) return;

            string[] lines = File.ReadAllLines(ini);
            bool changed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();

                // Doorstop 3 spells it targetAssembly; Doorstop 4 spells it target_assembly. Handle
                // both so this keeps working if the patch ever ships a newer doorstop.
                bool isTarget = trimmed.StartsWith("targetAssembly", StringComparison.OrdinalIgnoreCase)
                             || trimmed.StartsWith("target_assembly", StringComparison.OrdinalIgnoreCase);
                if (!isTarget) continue;

                int eq = lines[i].IndexOf('=');
                if (eq < 0) continue;

                string current = lines[i].Substring(eq + 1).Trim();
                if (string.Equals(current, OurTarget, StringComparison.OrdinalIgnoreCase)) return;

                lines[i] = lines[i].Substring(0, eq + 1) + OurTarget;
                changed = true;
                BootLog.Info("Repaired doorstop_config.ini: targetAssembly was '" + current + "', now '" + OurTarget + "'.");
                break;
            }

            if (changed) File.WriteAllLines(ini, lines);
        }

        /// <summary>
        /// Whether doorstop took its target from the command line rather than from the ini.
        ///
        /// Presence of the flag is the whole test, and it is sufficient on its own: doorstop prefers
        /// the command line over the ini, so whatever the ini says cannot affect this launch either
        /// way. Both spellings are accepted, matching the two key names handled above -- Doorstop 3
        /// takes --doorstop-target, Doorstop 4 --doorstop-target-assembly.
        ///
        /// Reading the process command line goes through the CLR only and never touches Unity, so it
        /// is safe at doorstop time.
        /// </summary>
        private static bool TargetedOnCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (args == null) return false;

                for (int i = 0; i < args.Length; i++)
                {
                    if (IsBlank(args[i])) continue;

                    string flag = args[i].TrimStart('-', '/');
                    if (flag.StartsWith("doorstop-target", StringComparison.OrdinalIgnoreCase)
                     || flag.StartsWith("doorstop_target", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception e)
            {
                // Unreadable command line: fall through to the ini repair rather than leave a
                // launch-option install unable to repair itself.
                BootLog.Warn("Could not read the command line (" + e.Message + "); assuming the ini install.");
            }

            return false;
        }

        // ---- Helpers ---------------------------------------------------------------------------

        /// <summary>
        /// Doorstop hands us the full path to Audiosurf2.exe. Falling back to this assembly's own
        /// location covers being invoked some other way; AS2ModLoader\ sits directly in the root.
        /// </summary>
        private static string GameRoot()
        {
            try
            {
                string exe = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
                if (!IsBlank(exe))
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (!IsBlank(dir)) return dir;
                }
            }
            catch { }

            try
            {
                string here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!IsBlank(here))
                {
                    string parent = Path.GetDirectoryName(here);
                    if (!IsBlank(parent)) return parent;
                }
            }
            catch { }

            return Directory.GetCurrentDirectory();
        }

        /// <summary>.NET 3.5 has no string.IsNullOrWhiteSpace.</summary>
        internal static bool IsBlank(string s)
        {
            if (s == null) return true;
            for (int i = 0; i < s.Length; i++)
                if (!char.IsWhiteSpace(s[i])) return false;
            return true;
        }
    }
}

namespace Doorstop
{
    /// <summary>
    /// Entry point for Doorstop 4, which calls "Doorstop.Entrypoint:Start" instead of "*:Main".
    /// The community patch currently ships Doorstop 3.4.1, so this is not the live path -- it means
    /// the same build keeps working if the patch ever updates its doorstop.
    /// </summary>
    public static class Entrypoint
    {
        public static void Start()
        {
            AS2.Bootstrap.Bootstrap.Boot();
        }
    }
}
