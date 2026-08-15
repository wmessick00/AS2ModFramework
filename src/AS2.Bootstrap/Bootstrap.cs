using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

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

            // Logging is initialised before the root is checked, and is the one thing allowed to
            // fall back to the working directory: writing a log file there is harmless, and
            // without it a failure to locate the game would have nowhere to report itself.
            try { BootLog.Init(Path.Combine(root ?? Directory.GetCurrentDirectory(), "AS2ModLoader")); }
            catch { /* logging is best-effort */ }

            if (root == null)
            {
                BootLog.Error("Could not determine the game folder: neither DOORSTOP_PROCESS_PATH nor this "
                            + "assembly's own location resolved. Refusing to load code relative to the working "
                            + "directory; no plugins will load this run.", null);
                return;
            }

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

            // The shipped PatchUpdaterPreloader.dll is built as a class library, so EntryPoint is
            // null and the search below is the live path on every launch, not a fallback. Its one
            // static Main is PatchUpdaterPreloader.Program.Main(string[]).
            //
            // That named type is therefore tried first, and only then does this fall back to
            // scanning. The scan is kept because the alternative is worse: the community patch
            // renaming a class it owns should not silently cost the player their auto-updater. But
            // it now logs which type it chose, so "we invoked something unexpected in the patch's
            // assembly" is a line in bootstrap.log rather than a thing nobody can see.
            MethodInfo entry = asm.EntryPoint;
            if (entry == null) entry = FindMain(asm.GetType("PatchUpdaterPreloader.Program", false));

            if (entry == null)
            {
                foreach (Type t in SafeGetTypes(asm))
                {
                    entry = FindMain(t);
                    if (entry != null)
                    {
                        BootLog.Warn("Patch preloader has no PatchUpdaterPreloader.Program.Main; using "
                                   + t.FullName + ".Main instead. The community patch has probably been "
                                   + "restructured -- worth checking that auto-update still works.");
                        break;
                    }
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

        /// <summary>A type's static Main, public or not, or null -- including when the type is null.</summary>
        private static MethodInfo FindMain(Type t)
        {
            if (t == null) return null;
            return t.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        /// <summary>
        /// Every type in an assembly, keeping whatever loaded when some of it did not.
        ///
        /// <para>
        /// Assembly.GetTypes throws ReflectionTypeLoadException whenever any type in the assembly
        /// references something that will not resolve, which is ordinary for a third-party assembly
        /// built against a slightly different set of dependencies. That exception is not a failure
        /// to enumerate: it carries a Types array holding every type that *did* load, with a null in
        /// each slot that did not.
        /// </para>
        ///
        /// <para>
        /// Letting it escape defeats the very thing this scan is here for. The caller falls back to
        /// scanning because the community patch renaming a class it owns should not cost the player
        /// their auto-updater; an unhandled throw here is caught far upstream as "could not chain to
        /// the community patch preloader", and the updater silently stops running for that launch.
        /// The Main we are looking for is very likely among the types that loaded fine.
        /// </para>
        /// </summary>
        private static Type[] SafeGetTypes(Assembly asm)
        {
            if (asm == null) return new Type[0];

            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                Type[] loaded = Salvage(e.Types);
                int total = e.Types == null ? 0 : e.Types.Length;

                BootLog.Warn("The patch preloader assembly only partly loaded; searching the "
                           + loaded.Length + " of " + total + " type(s) that did. This usually means "
                           + "it references something that is not present in this install.");

                return loaded;
            }
            catch (Exception e)
            {
                BootLog.Warn("Could not list the patch preloader's types: " + e.Message);
                return new Type[0];
            }
        }

        /// <summary>
        /// The entries of a partially loaded type array that actually loaded.
        ///
        /// Separate from <see cref="SafeGetTypes"/> so it can be exercised without an assembly that
        /// fails to load, which is not something a cold check can conjure.
        /// </summary>
        internal static Type[] Salvage(Type[] types)
        {
            if (types == null) return new Type[0];

            int kept = 0;
            for (int i = 0; i < types.Length; i++)
                if (types[i] != null) kept++;

            var result = new Type[kept];
            int next = 0;
            for (int i = 0; i < types.Length; i++)
                if (types[i] != null) result[next++] = types[i];

            return result;
        }

        // ---- Doorstop config ------------------------------------------------------------------

        /// <summary>
        /// How hard to try for exclusive access to doorstop_config.ini before leaving it for the
        /// next launch. Deliberately short: this runs before Unity starts, so every millisecond
        /// spent waiting here is a millisecond the player spends looking at nothing.
        /// </summary>
        private const int IniAttempts = 3;
        private const int IniRetryDelayMs = 50;

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
        ///
        /// The read and the write share one handle, opened with FileShare.None, because this file
        /// has a second writer and we know who it is. The comment on PatchPreloaderRelative says it:
        /// the old ModSettings build that used to hold this slot re-asserts itself three times a
        /// session, and a patch update rewrites the file outright. Read, close, then write back, and
        /// whatever the other process wrote in between is gone -- or worse, two writers land
        /// together and the file that decides whether any mod loads is neither version. An exclusive
        /// handle turns that into a sharing violation, which is a short retry rather than a loss.
        ///
        /// This and the three helpers under it are internal rather than private so that
        /// tests/AS2.Bootstrap.Tests can compile this file and drive them directly. None of this
        /// touches Unity or BepInEx, so it is testable cold; before those tests existed the only way
        /// to exercise it was to launch the game and read bootstrap.log.
        /// </summary>
        internal static void EnsureDoorstopTarget(string root)
        {
            if (TargetedOnCommandLine())
            {
                BootLog.Info("Doorstop was targeted on the command line; leaving doorstop_config.ini untouched.");
                return;
            }

            string ini = Path.Combine(root, "doorstop_config.ini");
            if (!File.Exists(ini)) return;

            for (int attempt = 1; attempt <= IniAttempts; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        string[] lines = ReadLines(stream);

                        string previous = Retarget(lines);
                        if (previous == null) return;

                        WriteLines(stream, lines);
                        BootLog.Info("Repaired doorstop_config.ini: targetAssembly was '" + previous
                                   + "', now '" + OurTarget + "'.");
                    }

                    return;
                }
                catch (FileNotFoundException)
                {
                    // Gone between the existence test and the open. A patch update replacing the
                    // file is the likely reason, and it wins: there is nothing here to repair.
                    return;
                }
                catch (IOException e)
                {
                    // Another process holds the file. Most likely the patch updater we invoked a
                    // moment ago, so a short wait is worth more than a whole launch without the
                    // repair -- but only a short one, and only because the launch option install
                    // never reaches this code at all.
                    if (attempt == IniAttempts)
                    {
                        BootLog.Warn("Could not rewrite doorstop_config.ini; another process most likely holds it ("
                                   + e.Message + "). Leaving it for the next launch.");
                        return;
                    }

                    Thread.Sleep(IniRetryDelayMs);
                }
            }
        }

        /// <summary>
        /// Points the target key at this bootstrap, and returns what the key said before.
        ///
        /// Null means there is nothing to write: either the key already names us, or the file has no
        /// target key at all. Adding one is not this method's job. A config with no target key is not
        /// the file doorstop launched this game from, and writing a new key into a file the community
        /// patch owns would guess at a format nobody asked us to produce.
        /// </summary>
        internal static string Retarget(string[] lines)
        {
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
                if (string.Equals(current, OurTarget, StringComparison.OrdinalIgnoreCase)) return null;

                lines[i] = lines[i].Substring(0, eq + 1) + OurTarget;
                return current;
            }

            return null;
        }

        /// <summary>
        /// Every line of the file, read through the handle the caller already holds.
        ///
        /// The reader is not disposed on purpose. Disposing it closes the stream the caller still
        /// has to write to, and .NET 3.5 has no leaveOpen overload to say otherwise; the caller's
        /// using block owns the stream. UTF-8 with byte order mark detection, which is what
        /// File.ReadAllLines did here before.
        /// </summary>
        internal static string[] ReadLines(FileStream stream)
        {
            var lines = new List<string>();
            var reader = new StreamReader(stream, Encoding.UTF8, true);

            string line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);

            return lines.ToArray();
        }

        /// <summary>
        /// Writes the lines back over the same handle.
        ///
        /// The new content goes down first and the file is truncated afterwards, rather than the
        /// other way round. Truncating first leaves a moment where doorstop_config.ini is empty on
        /// disk, and a machine that loses power in that moment costs the player the mod loader and
        /// the patch updater together. This ordering can leave a fragment of the longer old file
        /// behind instead, which doorstop reads as one unknown key.
        ///
        /// UTF-8 with no byte order mark, and Environment.NewLine between lines, matching what
        /// File.WriteAllLines wrote here before.
        /// </summary>
        internal static void WriteLines(FileStream stream, string[] lines)
        {
            stream.Position = 0;

            var writer = new StreamWriter(stream, new UTF8Encoding(false));
            for (int i = 0; i < lines.Length; i++) writer.WriteLine(lines[i]);
            writer.Flush();

            stream.SetLength(stream.Position);
            stream.Flush();
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
        internal static bool TargetedOnCommandLine()
        {
            try
            {
                return TargetedOnCommandLine(Environment.GetCommandLineArgs());
            }
            catch (Exception e)
            {
                // Unreadable command line: fall through to the ini repair rather than leave a
                // launch-option install unable to repair itself.
                BootLog.Warn("Could not read the command line (" + e.Message + "); assuming the ini install.");
                return false;
            }
        }

        /// <summary>
        /// The scan itself, over an argument list the caller supplies.
        ///
        /// Split from the method above so a test can hand it a launch's worth of arguments. The
        /// process command line is not something a test can set, and this decides whether the ini is
        /// written at all, so it is the one branch of the repair that would otherwise stay unproven.
        /// </summary>
        internal static bool TargetedOnCommandLine(string[] args)
        {
            if (args == null) return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (IsBlank(args[i])) continue;

                string flag = args[i].TrimStart('-', '/');
                if (flag.StartsWith("doorstop-target", StringComparison.OrdinalIgnoreCase)
                 || flag.StartsWith("doorstop_target", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // ---- Helpers ---------------------------------------------------------------------------

        /// <summary>
        /// Doorstop hands us the full path to Audiosurf2.exe. Falling back to this assembly's own
        /// location covers being invoked some other way; AS2ModLoader\ sits directly in the root.
        ///
        /// Returns null rather than the working directory when neither resolves. Everything the
        /// caller does with this value loads or rewrites code -- Assembly.LoadFrom on two
        /// preloaders, and the doorstop target ini -- and a working directory is not ours to
        /// assume: it is whatever the process was started from, which a shortcut or a launcher
        /// chooses. Both branches above succeed in every real launch, so this costs nothing;
        /// it exists so that "we do not know where the game is" cannot quietly become
        /// "load BepInEx\core\BepInEx.Preloader.dll from wherever we happen to be standing".
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

            return null;
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
