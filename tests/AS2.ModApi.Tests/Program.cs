using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>
    /// Cold checks for TargetResolver's path and key logic.
    ///
    /// These matter most for the escape rejections. FolderForKey and Normalize are what stop a
    /// key -- which is built from a Steam Workshop folder name, or read back out of some mod's
    /// saved JSON -- from naming a location outside the install, whether it says so in the path or
    /// hides it behind a junction. That is exactly the kind of guard a later refactor removes by
    /// accident, and without this it would take a manual launch and a careful read of the log to
    /// notice.
    ///
    /// Run with `dotnet run --project tests/AS2.ModApi.Tests`. Exit code 0 means everything passed.
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Junctions = new List<string>();
        private static string _root;
        private static string _outside;

        private static int Main()
        {
            string stem = Path.Combine(Path.GetTempPath(), "as2-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _root = stem;
            _outside = stem + "-outside";
            Paths.GameRootPath = _root;

            try
            {
                BuildFixture();

                NormalizeAcceptsWhatTheGameHandsOut();
                NormalizeRejectsEscapes();
                NormalizeResolvesCasingAgainstDisk();
                KeyForFolderContainment();
                FolderForKeyContainment();
                FileNameGuards();
                EnumerateFindsEveryFolderShape();
                NegativeCasingResultsAreNotCached();
                LinkedFoldersAreNotFollowed();

                SurfaceChecks.Run();
                StoreChecks.Run();
            }
            catch (Exception e)
            {
                Fail("A test threw, which is itself a failure: " + e);
            }
            finally
            {
                Cleanup();
            }

            return Report();
        }

        /// <summary>
        /// Junctions come out first and one at a time. Directory.Delete(recursive) is documented not
        /// to follow reparse points, but "documented not to" is a thin thing to bet somebody's temp
        /// folder on, and removing the link itself never touches what it points at.
        /// </summary>
        private static void Cleanup()
        {
            foreach (string junction in Junctions)
            {
                try { if (Directory.Exists(junction)) Directory.Delete(junction, false); } catch { }
            }
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            try { if (Directory.Exists(_outside)) Directory.Delete(_outside, true); } catch { }
        }

        // ---- The fixture --------------------------------------------------------------------
        //
        // Every folder shape TargetResolver documents, so Enumerate and the key builders are
        // exercised against the real thing rather than against one easy case.

        private const string Schema = "modsettings.lua";

        private static void BuildFixture()
        {
            MakeTarget("skins/Plain");                    // plain local skin
            MakeTarget("skins/123456/FromWorkshop");      // Workshop item, all-digit container
            MakeTarget("mods/mymode");                    // a mode
            MakeTarget("mods/mymode/skins/Dedicated");    // skin dedicated to that mode
            Directory.CreateDirectory(Path.Combine(_root, "skins", "NoSchema"));  // must be ignored
        }

        private static void MakeTarget(string relative)
        {
            string dir = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Schema), "-- fixture");
        }

        private static string Abs(string relative)
        {
            return Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        // ---- Normalize ----------------------------------------------------------------------

        private static void NormalizeAcceptsWhatTheGameHandsOut()
        {
            // The game reports selectedSkinRelativePath with a leading slash, and is not consistent
            // about separators or doubled ones.
            Same("Normalize strips the game's leading slash", TargetResolver.Normalize("/skins/Plain"), "skins/Plain");
            Same("Normalize converts backslashes", TargetResolver.Normalize(@"skins\Plain"), "skins/Plain");
            Same("Normalize collapses doubled separators", TargetResolver.Normalize("skins//Plain"), "skins/Plain");
            Same("Normalize handles a nested key", TargetResolver.Normalize("/mods/mymode/skins/Dedicated"),
                 "mods/mymode/skins/Dedicated");

            Null("Normalize(null) is null", TargetResolver.Normalize(null));
            Null("Normalize(\"\") is null", TargetResolver.Normalize(""));
            Null("Normalize(\"   \") is null", TargetResolver.Normalize("   "));
            Null("Normalize(\"/\") is null", TargetResolver.Normalize("/"));

            // A key naming nothing on disk still passes through: this must never turn a usable key
            // into null just because the folder is missing.
            Same("Normalize keeps an unknown key rather than nulling it",
                 TargetResolver.Normalize("skins/NotInstalled"), "skins/NotInstalled");
        }

        private static void NormalizeRejectsEscapes()
        {
            foreach (string bad in new[] { "../outside", "skins/../../outside", "./skins/Plain", "skins/./Plain", ".." })
            {
                ModApiPlugin.Log.Clear();
                Null("Normalize rejects '" + bad + "'", TargetResolver.Normalize(bad));
                True("Normalize says why it refused '" + bad + "'", ModApiPlugin.Log.Mentions("Refusing"));
            }

            foreach (string rooted in new[] { "C:/Windows", "C:/Windows/System32", "c:/windows", "skins/Plain:stream" })
            {
                ModApiPlugin.Log.Clear();
                Null("Normalize rejects the absolute or stream-qualified '" + rooted + "'",
                     TargetResolver.Normalize(rooted));
                True("Normalize says why it refused '" + rooted + "'", ModApiPlugin.Log.Mentions("Refusing"));
            }
        }

        private static void NormalizeResolvesCasingAgainstDisk()
        {
            // The reason this method exists: the game reports the same skin two ways, and a key that
            // forks in spelling strands a mod's saved settings.
            Same("Normalize corrects a lowercased key to the on-disk casing",
                 TargetResolver.Normalize("skins/plain"), "skins/Plain");
            Same("Normalize corrects an uppercased key",
                 TargetResolver.Normalize("SKINS/PLAIN"), "skins/Plain");
            Same("Normalize corrects casing at every level",
                 TargetResolver.Normalize("MODS/MYMODE/SKINS/DEDICATED"), "mods/mymode/skins/Dedicated");
        }

        // ---- Containment --------------------------------------------------------------------

        private static void KeyForFolderContainment()
        {
            Same("KeyForFolder builds a key for a folder in the install",
                 TargetResolver.KeyForFolder(Abs("skins/Plain")), "skins/Plain");

            Null("KeyForFolder refuses a folder outside the install",
                 TargetResolver.KeyForFolder(@"C:\Windows\System32"));

            // The trailing-separator case the RootPrefix comment calls out: a sibling install whose
            // name merely starts with the root's would pass a naive StartsWith.
            Null("KeyForFolder refuses a sibling whose path starts with the root",
                 TargetResolver.KeyForFolder(_root + " Backup"));

            Null("KeyForFolder(null) is null", TargetResolver.KeyForFolder(null));
        }

        private static void FolderForKeyContainment()
        {
            Same("FolderForKey resolves a good key to its folder",
                 TargetResolver.FolderForKey("skins/Plain"), Abs("skins/Plain"));

            foreach (string escape in new[] { "../outside", "../../Windows", "skins/../../outside" })
            {
                ModApiPlugin.Log.Clear();
                Null("FolderForKey refuses the traversal '" + escape + "'", TargetResolver.FolderForKey(escape));
            }

            foreach (string rooted in new[] { "C:/Windows", @"C:\Windows" })
            {
                ModApiPlugin.Log.Clear();
                // Path.Combine returns a rooted second argument whole and throws the root away, so
                // this is the case that most needs the containment test rather than a dot-segment scan.
                Null("FolderForKey refuses the rooted key '" + rooted + "'", TargetResolver.FolderForKey(rooted));
                True("FolderForKey says why it refused '" + rooted + "'", ModApiPlugin.Log.Mentions("outside the game root"));
            }

            Null("FolderForKey(null) is null", TargetResolver.FolderForKey(null));
        }

        // ---- File-name guards ----------------------------------------------------------------

        private static void FileNameGuards()
        {
            True("HasFile finds a schema that is really there", TargetResolver.HasFile("skins/Plain", Schema));
            False("HasFile is false for a folder without one", TargetResolver.HasFile("skins/NoSchema", Schema));

            foreach (string bad in new[] { "../secrets.json", @"..\secrets.json", "sub/dir.json", @"C:\Windows\win.ini", "..", "" })
            {
                ModApiPlugin.Log.Clear();
                False("HasFile refuses the non-plain name '" + bad + "'", TargetResolver.HasFile("skins/Plain", bad));
            }

            // An absolute file name would make the File.Exists test true for every folder inspected,
            // so Enumerate would answer "all of them" -- a wrong answer that looks like a working one.
            string everywhere = Path.Combine(Abs("skins/Plain"), Schema);
            ModApiPlugin.Log.Clear();
            List<Target> bogus = TargetResolver.Enumerate(everywhere);
            True("Enumerate refuses an absolute file name", bogus != null && bogus.Count == 0);
            True("Enumerate says why it refused", ModApiPlugin.Log.Mentions("Refusing"));

            DeviceNamesAreRefused();
        }

        // ---- Regression: issue #15 -------------------------------------------------------------

        /// <summary>
        /// Win32 keeps the DOS device names whatever extension follows them, so "nul.json" is the
        /// NUL device rather than a file. Writing a mod's data there throws nothing and leaves
        /// nothing: the guard is the only thing between a Workshop folder called "nul" and data
        /// that disappears every session.
        /// </summary>
        private static void DeviceNamesAreRefused()
        {
            DeviceWritesReallyDisappear();

            foreach (string device in new[] { "nul", "NUL", "nul.json", "nul.", "nul .txt", "con", "CON.txt",
                                              "aux", "prn.dat", "com1", "COM9.log", "lpt1", "conin$", "conout$.json" })
            {
                ModApiPlugin.Log.Clear();
                False("HasFile refuses the device name '" + device + "'", TargetResolver.HasFile("skins/Plain", device));
                True("HasFile says why it refused '" + device + "'", ModApiPlugin.Log.Mentions("device name"));
            }

            // Only the whole name in front of the first dot is a device. A guard that refused these
            // would be a new bug rather than a fix.
            foreach (string ordinary in new[] { "console.json", "nullable.json", "com.json", "com10.json",
                                                "lpt.json", "auxiliary.json", "skin-nul.json" })
            {
                ModApiPlugin.Log.Clear();
                TargetResolver.HasFile("skins/Plain", ordinary);
                False("HasFile accepts the ordinary name '" + ordinary + "'",
                      ModApiPlugin.Log.Mentions("plain file name"));
            }

            ModApiPlugin.Log.Clear();
            List<Target> devices = TargetResolver.Enumerate("nul.json");
            True("Enumerate refuses a device name", devices != null && devices.Count == 0);
            True("Enumerate says why it refused a device name", ModApiPlugin.Log.Mentions("device name"));
        }

        /// <summary>
        /// The hazard itself, probed rather than assumed, because how far it reaches depends on the
        /// machine.
        ///
        /// Win32 has always mapped a device name to the device whatever extension follows it, and
        /// Microsoft still documents "NUL.txt" that way. Windows 11 build 26200 does not: there,
        /// "nul.json" and "con.txt" are ordinary files -- checked through both cmd.exe and
        /// File.WriteAllText -- while a bare "nul" still swallows the write. A player on Windows 10
        /// therefore loses the data that a player on 26200 keeps.
        ///
        /// That split is the argument for the guard rather than a reason to doubt it: a file name is
        /// not something a mod can test on its author's machine and rely on. So this reports which
        /// machine it ran on and never fails. A skip means this build kept the file; the rejections
        /// below hold either way, because the guard refuses the name and does not ask the OS.
        /// </summary>
        private static void DeviceWritesReallyDisappear()
        {
            foreach (string name in new[] { "nul.json", "nul" })
            {
                string sink = Path.Combine(Abs("skins/Plain"), name);

                try
                {
                    File.WriteAllText(sink, "data a mod would expect to read back next launch");
                }
                catch (Exception e)
                {
                    Pass("'" + name + "' is no ordinary file name here: the write threw " + e.GetType().Name);
                    return;
                }

                if (!File.Exists(sink))
                {
                    Pass("a write to '" + name + "' really does vanish, which is what the guard is for");
                    return;
                }

                try { File.Delete(sink); } catch { }
            }

            Skip("The device-name hazard did not reproduce: this machine wrote real files for both "
               + "'nul.json' and 'nul'. Windows 10 loses both, so the guard still has to refuse them.");
        }

        // ---- Enumerate ------------------------------------------------------------------------

        private static void EnumerateFindsEveryFolderShape()
        {
            List<Target> found = TargetResolver.Enumerate(Schema);

            var keys = new List<string>();
            foreach (Target t in found) keys.Add(t.Key);

            True("Enumerate finds the plain skin", keys.Contains("skins/Plain"));
            True("Enumerate descends into an all-digit Workshop container", keys.Contains("skins/123456/FromWorkshop"));
            True("Enumerate finds the mode", keys.Contains("mods/mymode"));
            True("Enumerate finds a mode's dedicated skin", keys.Contains("mods/mymode/skins/Dedicated"));
            False("Enumerate skips a folder with no schema", keys.Contains("skins/NoSchema"));
            Same("Enumerate found exactly the four fixtures", found.Count.ToString(), "4");

            // Skins first, then modes; that ordering is what keeps a settings list stable.
            True("Enumerate returns skins before modes",
                 found.Count == 4 && found[0].Kind == SelectorKind.Skin && found[3].Kind == SelectorKind.Mode);

            foreach (Target t in found)
                True("Enumerate's FolderPath for " + t.Key + " exists", Directory.Exists(t.FolderPath));
        }

        // ---- Regression: issue #2 -------------------------------------------------------------

        private static void NegativeCasingResultsAreNotCached()
        {
            // A Workshop item that is still downloading when something first asks about it. Caching
            // the failed lookup pinned the asker's spelling for the rest of the process, so the
            // folder's real casing was never picked up again that session.
            const string key = "skins/999888/LateArrival";

            Same("before it lands, the key passes through unchanged",
                 TargetResolver.Normalize(key), "skins/999888/LateArrival");

            // It arrives, and Steam spells the folder differently from the caller.
            Directory.CreateDirectory(Path.Combine(_root, "skins", "999888", "latearrival"));

            Same("once it lands, the next call picks up the on-disk casing",
                 TargetResolver.Normalize(key), "skins/999888/latearrival");
        }

        // ---- Regression: issue #13 -------------------------------------------------------------

        /// <summary>
        /// Containment used to be decided by string comparison alone, which cannot see a junction:
        /// "&lt;root&gt;\skins\Linked" reads as inside the install whatever it really points at, and
        /// every path call underneath -- GetDirectories, File.Exists, whatever a mod does with the
        /// folder afterwards -- follows it out without a word. Creating a junction on Windows needs
        /// no elevation, so this is not a privileged trick either.
        ///
        /// This runs last because it adds link fixtures the earlier counts do not expect.
        /// </summary>
        private static void LinkedFoldersAreNotFollowed()
        {
            // What the links point at: a schema at the top, and one a level down, so both a link at
            // the target folder and a link on the way to it are covered.
            Directory.CreateDirectory(_outside);
            File.WriteAllText(Path.Combine(_outside, Schema), "-- outside the install");
            Directory.CreateDirectory(Path.Combine(_outside, "Deep"));
            File.WriteAllText(Path.Combine(_outside, "Deep", Schema), "-- outside the install");

            // A mode whose dedicated skins folder is the link. That folder is composed rather than
            // listed, so it is the one container the subdirectory filter never sees.
            Directory.CreateDirectory(Abs("mods/linkedmode"));

            if (!TryMakeJunction(Abs("skins/Linked"), _outside) ||
                !TryMakeJunction(Abs("mods/linkedmode/skins"), _outside))
            {
                Skip("Link containment is unverified: this machine would not create a junction. " +
                     "CI runs on Windows, where mklink /J needs no elevation.");
                return;
            }

            // Without this the rest could pass because nothing was following anything. The junction
            // has to really work for the checks below to mean what they say.
            True("the fixture junction really does resolve",
                 File.Exists(Path.Combine(Abs("skins/Linked"), Schema)));

            ModApiPlugin.Log.Clear();
            Null("KeyForFolder refuses a linked folder inside the install",
                 TargetResolver.KeyForFolder(Abs("skins/Linked")));
            True("KeyForFolder says the folder was a link",
                 ModApiPlugin.Log.Mentions("junction or symbolic link"));

            Null("KeyForFolder refuses a folder reached through a link",
                 TargetResolver.KeyForFolder(Abs("skins/Linked/Deep")));

            ModApiPlugin.Log.Clear();
            Null("FolderForKey refuses a key naming a linked folder", TargetResolver.FolderForKey("skins/Linked"));
            True("FolderForKey says the key went through a link",
                 ModApiPlugin.Log.Mentions("junction or symbolic link"));

            Null("FolderForKey refuses a key that only passes through a link",
                 TargetResolver.FolderForKey("skins/Linked/Deep"));

            False("HasFile does not report a schema behind a link",
                  TargetResolver.HasFile("skins/Linked", Schema));

            // Adopting the on-disk casing would be this API saying the folder is install content.
            Same("Normalize does not adopt a linked folder's casing",
                 TargetResolver.Normalize("skins/linked"), "skins/linked");

            List<Target> found = TargetResolver.Enumerate(Schema);
            var keys = new List<string>();
            foreach (Target t in found) keys.Add(t.Key);

            False("Enumerate does not list a linked skin folder", keys.Contains("skins/Linked"));
            False("Enumerate does not descend through a mode's linked skins folder",
                  keys.Contains("mods/linkedmode/skins/Deep"));
            Same("Enumerate still found exactly the four real fixtures", found.Count.ToString(), "4");
        }

        /// <summary>
        /// Creates an NTFS junction, or returns false if this machine will not make one.
        ///
        /// A junction rather than a symbolic link because creating one needs neither elevation nor
        /// developer mode -- which is precisely why it is worth guarding against -- and mklink is
        /// the only way to get one without P/Invoke.
        /// </summary>
        private static bool TryMakeJunction(string link, string target)
        {
            try
            {
                var startInfo = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(startInfo))
                {
                    // Read before waiting: a full pipe buffer would deadlock the wait.
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                }

                if (!Directory.Exists(link)) return false;
                Junctions.Add(link);
                return true;
            }
            catch { return false; }
        }
    }
}
