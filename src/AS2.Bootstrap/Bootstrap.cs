using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AS2.Bootstrap
{
    // The doorstop bridge
    // ===========================================================================================
    // Doorstop 3.4.1 (the patch's)   resolves "*:Main"
    // Doorstop 4.5.0 (BepInEx's)     exposes "Doorstop.Entrypoint:Start"
    // The two do not meet, so BepInEx cannot be the doorstop target directly
    // Running BepInEx's installer instead overwrites winhttp.dll and doorstop_config.ini, which
    // the patch reverts on its next update, silently and permanently
    // Full argument: see The Loading Chain wiki page

    /// <summary>Doorstop entry point that hands control to BepInEx</summary>
    // Every step below is try-caught on its own. A mod loader that fails to start must not stop
    // the game starting, and must not stop the patch updating itself
    public static class Bootstrap
    {
        private const string OurTarget = @"AS2ModLoader\AS2.Bootstrap.dll";
        private const string PreloaderRelative = @"BepInEx\core\BepInEx.Preloader.dll";

        /// <summary>The community patch's own preloader, which launches PatchUpdater.exe</summary>
        // We took the single doorstop slot, so we invoke it and patch auto-update keeps working
        // A constant, not "whatever we displaced from the ini" -- reading the displaced value back
        // would resurrect a competing mod loader
        // The old ModSettings build in that slot re-asserts itself 3 times a session, so it would
        // fight for the slot on every launch
        private const string PatchPreloaderRelative = @"Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll";

        private static bool _booted;

        /// <summary>Entry point for the community patch's Doorstop 3.4.1, descriptor "*:Main"</summary>
        public static void Main()
        {
            Boot();
        }

        internal static void Boot()
        {
            if (_booted) return;
            _booted = true;

            string root = GameRoot();

            // Logging starts before the root is checked, and is the one thing allowed to fall
            // back to the working directory
            // A log file there is harmless, and without it a failure to find the game would have
            // nowhere to report itself
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

            // The patch updater goes first, on purpose
            // If BepInEx is broken, the patch must still be able to update itself out of it
            try { ChainToPatchPreloader(root); }
            catch (Exception e) { BootLog.Error("Could not chain to the community patch preloader.", e); }

            try { EnsureDoorstopTarget(root); }
            catch (Exception e) { BootLog.Error("Could not re-assert the doorstop target.", e); }

            try { StartBepInEx(root); }
            catch (Exception e) { BootLog.Error("Could not start BepInEx; no plugins will load this run.", e); }
        }

        // ---- BepInEx ------------------------------------------------------------------------

        /// <summary>Loads BepInEx's preloader and calls Doorstop.Entrypoint.Start() on it</summary>
        // DOORSTOP_INVOKE_DLL_PATH has to be fixed up first. BepInEx derives its whole layout from it:
        //     preloaderPath = GetDirectoryName(GetFullPath(DOORSTOP_INVOKE_DLL_PATH))
        //     bepinPath     = ParentDirectory(GetFullPath(DOORSTOP_INVOKE_DLL_PATH), 2)
        // Doorstop set it to this assembly, so left alone BepInEx reads its root as AS2ModLoader        // and finds no plugins
        // Pointed at the preloader it gives BepInEx\core and BepInEx, which is a stock install
        // The other 3 describe the game, not the invoked assembly, and 3.4.1 sets them correctly
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

        /// <summary>Every type in an assembly, keeping whatever loaded when some of it did not</summary>
        // Assembly.GetTypes throws ReflectionTypeLoadException when any type references something
        // that will not resolve, which is ordinary for a third-party assembly
        // That is not a failure to enumerate: the exception carries a Types array holding every
        // type that did load, with a null in each slot that did not
        // Letting it escape defeats the point of the scan. The caller scans because the patch
        // renaming its own class should not cost the player the auto-updater
        // An unhandled throw here surfaces upstream as "could not chain to the community patch
        // preloader", and the updater silently stops for that launch
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

        /// <summary>The entries of a partly loaded type array that actually loaded</summary>
        // Separate from <see cref="SafeGetTypes"/> so a cold check can drive it without an
        // assembly that fails to load, which is not something a test can conjure
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

        /// <summary>How hard to try for exclusive access to doorstop_config.ini</summary>
        // 3 tries 50 ms apart, then leave the file for the next launch
        // Short on purpose: this runs before Unity starts, so the wait is time the player spends
        // looking at nothing
        private const int IniAttempts = 3;
        private const int IniRetryDelayMs = 50;

        /// <summary>Re-asserts targetAssembly in doorstop_config.ini, that one key only</summary>
        // Fallback for the ini install, not the primary one. The file is in the patch's payload,
        // so a patch update reverts it and this repair lands on the launch after the one that broke
        // The durable install is the Steam launch option, which command-line values win with:
        //     --doorstop-target "<game>\AS2ModLoader\AS2.Bootstrap.dll"
        // So this does nothing when we were launched that way. Writing then would edit a file the
        // community patch owns, to no effect, which that project is entitled to object to
        //
        // The read and the write share one FileShare.None handle, because this file has a second
        // writer: the old ModSettings build re-asserts itself 3 times a session, and a patch update
        // rewrites it outright
        // Read, close, then write back and the other writer's content is gone -- or the two land
        // together and the file that decides whether any mod loads is neither version
        // An exclusive handle makes that a sharing violation, which is a retry rather than a loss
        //
        // This and the 3 helpers below are internal, not private, so tests/AS2.Bootstrap.Tests can
        // compile this file and drive them cold. None of it touches Unity or BepInEx
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

        /// <summary>Points the target key at this bootstrap, and returns what it said before</summary>
        // Null means nothing to write: the key already names us, or there is no target key at all
        // Adding one is not this method's job. A config with no target key is not the file doorstop
        // launched this game from, and inventing a key in a file the patch owns guesses at a format
        internal static string Retarget(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();

                // Doorstop 3 spells it targetAssembly, Doorstop 4 target_assembly
                // Handle both, so this keeps working if the patch ships a newer doorstop
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

        /// <summary>Every line of the file, read through the handle the caller already holds</summary>
        // The reader is not disposed on purpose -- that would close the stream the caller still has
        // to write to, and .NET 3.5 has no leaveOpen overload to say otherwise
        // The caller's using block owns the stream
        // UTF-8 with byte order mark detection, which is what File.ReadAllLines did here before
        internal static string[] ReadLines(FileStream stream)
        {
            var lines = new List<string>();
            var reader = new StreamReader(stream, Encoding.UTF8, true);

            string line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);

            return lines.ToArray();
        }

        /// <summary>Writes the lines back over the same handle</summary>
        // Content goes down first, truncate afterwards, not the other way round
        // Truncating first leaves a moment where doorstop_config.ini is empty on disk, and a
        // machine that loses power in it costs the player the mod loader and the patch updater
        // This ordering leaves a fragment of the longer old file instead, which doorstop reads as
        // one unknown key
        // UTF-8 with no byte order mark, Environment.NewLine between lines -- what
        // File.WriteAllLines wrote here before
        internal static void WriteLines(FileStream stream, string[] lines)
        {
            stream.Position = 0;

            var writer = new StreamWriter(stream, new UTF8Encoding(false));
            for (int i = 0; i < lines.Length; i++) writer.WriteLine(lines[i]);
            writer.Flush();

            stream.SetLength(stream.Position);
            stream.Flush();
        }

        /// <summary>Whether doorstop took its target from the command line, not the ini</summary>
        // Presence of the flag is the whole test, and it is enough on its own: doorstop prefers the
        // command line, so the ini cannot affect this launch either way
        // Both spellings, matching the two key names above -- Doorstop 3 takes --doorstop-target,
        // Doorstop 4 takes --doorstop-target-assembly
        // Reading the process command line goes through the CLR alone, so it is safe at doorstop time
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

        /// <summary>The scan itself, over an argument list the caller supplies</summary>
        // Split from the method above so a test can hand it a launch's worth of arguments
        // The process command line is not something a test can set, and this decides whether the
        // ini is written at all -- the one branch of the repair that would otherwise stay unproven
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

        /// <summary>The game root, from doorstop or from this assembly's own location</summary>
        // Doorstop hands over the full path to Audiosurf2.exe. The fallback covers being invoked
        // some other way, since AS2ModLoader\ sits directly in the root
        // Returns null rather than the working directory when neither resolves
        // Everything the caller does with this loads or rewrites code, and a working directory is
        // whatever the process was started from, which a shortcut or a launcher chooses
        // Both branches succeed in every real launch, so this costs nothing. It stops "we do not
        // know where the game is" becoming "load a preloader from wherever we are standing"
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
    /// <summary>Entry point for Doorstop 4, which calls "Doorstop.Entrypoint:Start"</summary>
    // Not the live path: the community patch ships Doorstop 3.4.1, which calls "*:Main"
    // It means the same build keeps working if the patch ever updates its doorstop
    public static class Entrypoint
    {
        public static void Start()
        {
            AS2.Bootstrap.Bootstrap.Boot();
        }
    }
}
