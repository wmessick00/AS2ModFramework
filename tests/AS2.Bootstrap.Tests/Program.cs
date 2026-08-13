using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using static AS2.Tests.Check;

namespace AS2.Bootstrap.Tests
{
    /// <summary>
    /// Cold checks for the doorstop_config.ini repair in AS2.Bootstrap.
    ///
    /// This file decides whether any mod loads at all, and it belongs to the community patch: the
    /// patch ships it, a patch update rewrites it, and the old ModSettings build re-asserts itself
    /// into it three times a session. So the repair has two jobs that pull against each other --
    /// point the target key at this bootstrap, and change nothing else, ever. Mis-reading the key
    /// would silently disable modding; truncating before writing would cost the player the mod
    /// loader and the patch updater together on a power loss.
    ///
    /// None of it needs Unity, BepInEx or the game. Until these checks existed the only way to
    /// exercise any of it was to launch the game and read bootstrap.log, which is exactly the manual
    /// round AGENTS.md says to spend on things that really do need the game.
    ///
    /// Run with `dotnet run --project tests/AS2.Bootstrap.Tests`. Exit code 0 means everything
    /// passed.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// The install layout this repair asserts, spelled out rather than read from Bootstrap's own
        /// constant. A check that took the value from the code under test would agree with any
        /// change to it, and this is the path the README tells a player to put in a Steam launch
        /// option. Changing it is a decision, so it should fail here first.
        /// </summary>
        private const string Target = @"AS2ModLoader\AS2.Bootstrap.dll";

        private static readonly string NL = Environment.NewLine;

        private static string _stem;
        private static int _roots;

        private static int Main()
        {
            _stem = Path.Combine(Path.GetTempPath(),
                                 "as2-bootstrap-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_stem);

            try
            {
                RetargetRewritesTheKey();
                RetargetLeavesEverythingElseAlone();
                RetargetDeclinesWhenThereIsNothingToDo();
                ReadLinesAndWriteLinesRoundTrip();
                WriteLinesTruncatesInBothDirections();
                CommandLineTargetIsRecognised();

                // The checks below drive the whole repair, which starts by asking whether doorstop
                // was targeted on the command line. That reads this process's own command line, so a
                // test run started with a doorstop flag would take the early return and prove
                // nothing. Saying so is better than quietly passing.
                if (Bootstrap.TargetedOnCommandLine())
                {
                    Skip("The end-to-end ini checks did not run: this test process was itself "
                       + "launched with a doorstop target flag, which is the one case where "
                       + "EnsureDoorstopTarget deliberately does nothing.");
                }
                else
                {
                    EnsureDoorstopTargetRepairsARealFile();
                    EnsureDoorstopTargetLeavesTheFileAlone();
                    EnsureDoorstopTargetGivesUpOnALockedFile();
                }
            }
            catch (Exception e)
            {
                Fail("A test threw, which is itself a failure: " + e);
            }
            finally
            {
                try { if (Directory.Exists(_stem)) Directory.Delete(_stem, true); } catch { }
            }

            return Report();
        }

        // ---- Retarget ---------------------------------------------------------------------------

        private static void RetargetRewritesTheKey()
        {
            string[] lines = { "[UnityDoorstop]", "enabled=true", @"targetAssembly=ModSettings\ModSettings.dll" };

            Same("Retarget returns what the key said before",
                 Bootstrap.Retarget(lines), @"ModSettings\ModSettings.dll");
            SameLines("Retarget rewrites the Doorstop 3 spelling", lines,
                      "[UnityDoorstop]", "enabled=true", "targetAssembly=" + Target);

            // Doorstop 4 renamed the key. The community patch ships 3.4.1, so this branch is one
            // only a test can reach until the day it becomes the live one.
            lines = new[] { "[General]", @"target_assembly=BepInEx\core\BepInEx.Preloader.dll" };
            Same("Retarget returns the previous Doorstop 4 value",
                 Bootstrap.Retarget(lines), @"BepInEx\core\BepInEx.Preloader.dll");
            SameLines("Retarget rewrites the Doorstop 4 spelling", lines,
                      "[General]", "target_assembly=" + Target);

            // The key and the whitespace around it are the patch's to spell. Only the text after the
            // '=' is ours, so only that is replaced.
            lines = new[] { "  TargetAssembly  =  old.dll  " };
            Same("Retarget reads the key whatever its casing", Bootstrap.Retarget(lines), "old.dll");
            SameLines("Retarget keeps the indent and the key's own spelling", lines,
                      "  TargetAssembly  =" + Target);
        }

        private static void RetargetLeavesEverythingElseAlone()
        {
            // A file shaped like a real one: a comment, two sections, a blank line, and other keys.
            // Every line but the one has to come back exactly, because every one of them belongs to
            // the community patch.
            string[] lines =
            {
                "# Doorstop settings",
                "[UnityDoorstop]",
                "enabled=true",
                @"targetAssembly=ModSettings\ModSettings.dll",
                "",
                "[MonoBackend]",
                "debugEnabled=false",
                "dllSearchPathOverride=",
            };

            Same("Retarget reports the displaced target",
                 Bootstrap.Retarget(lines), @"ModSettings\ModSettings.dll");
            SameLines("Retarget preserves comments, sections, blank lines and every other key", lines,
                      "# Doorstop settings",
                      "[UnityDoorstop]",
                      "enabled=true",
                      "targetAssembly=" + Target,
                      "",
                      "[MonoBackend]",
                      "debugEnabled=false",
                      "dllSearchPathOverride=");

            // A key with no value is still the key, and doorstop reads it as no target at all.
            // Filling it in is the repair working, not an edge case.
            lines = new[] { "targetAssembly=" };
            Same("Retarget reports an empty target as an empty string", Bootstrap.Retarget(lines), "");
            SameLines("Retarget fills in an empty target", lines, "targetAssembly=" + Target);

            // Only the first target key is rewritten. A file with two is malformed and doorstop
            // reads one of them; rewriting both would be this code deciding which, on no evidence.
            lines = new[] { "targetAssembly=first.dll", "targetAssembly=second.dll" };
            Same("Retarget takes the first target key", Bootstrap.Retarget(lines), "first.dll");
            SameLines("Retarget leaves a duplicate target key alone", lines,
                      "targetAssembly=" + Target, "targetAssembly=second.dll");

            // No '=' means no key/value pair, whatever the line starts with. The scan has to carry
            // on past it rather than stop, or one stray word would cost the whole repair.
            lines = new[] { "targetAssembly", "targetAssembly=real.dll" };
            Same("Retarget skips a target line with no '=' and keeps looking",
                 Bootstrap.Retarget(lines), "real.dll");
            SameLines("Retarget rewrites the line that really is a pair", lines,
                      "targetAssembly", "targetAssembly=" + Target);
        }

        private static void RetargetDeclinesWhenThereIsNothingToDo()
        {
            // Null means "do not write". EnsureDoorstopTarget returns on it without writing at all,
            // which is what stops every launch from rewriting a file it already agrees with.
            string[] lines = { "targetAssembly=" + Target };
            Null("Retarget declines when the key already names us", Bootstrap.Retarget(lines));
            SameLines("Retarget leaves an already-correct line alone", lines, "targetAssembly=" + Target);

            // Neither doorstop nor NTFS cares about the casing of a path, so a target that differs
            // only in spelling is not a repair either.
            lines = new[] { @"targetAssembly = as2modloader\as2.bootstrap.dll " };
            Null("Retarget declines a target that differs only in casing and spacing",
                 Bootstrap.Retarget(lines));
            SameLines("Retarget leaves that line alone too", lines,
                      @"targetAssembly = as2modloader\as2.bootstrap.dll ");

            // Adding a key is not this code's job: a file with no target key is not the file
            // doorstop launched this game from, and the format would be a guess.
            lines = new[] { "[UnityDoorstop]", "enabled=true" };
            Null("Retarget declines a file with no target key", Bootstrap.Retarget(lines));
            SameLines("Retarget adds nothing to a file with no target key", lines,
                      "[UnityDoorstop]", "enabled=true");

            // A commented-out key is a comment. Rewriting one would edit the patch's own notes and
            // still leave the real target wherever it was.
            foreach (string comment in new[] { "#targetAssembly=old.dll", ";targetAssembly=old.dll",
                                               "// targetAssembly=old.dll" })
            {
                lines = new[] { comment };
                Null("Retarget declines the commented-out '" + comment + "'", Bootstrap.Retarget(lines));
                SameLines("Retarget leaves '" + comment + "' as it found it", lines, comment);
            }

            Null("Retarget declines an empty file", Bootstrap.Retarget(new string[0]));
        }

        // ---- ReadLines and WriteLines -------------------------------------------------------------

        /// <summary>
        /// Both halves are driven over one handle, opened the way EnsureDoorstopTarget opens it,
        /// rather than through File.ReadAllLines. The shared handle is the whole point of these two
        /// methods existing -- see the comment on EnsureDoorstopTarget -- so a check that read the
        /// file some other way would be checking something else.
        /// </summary>
        private static void ReadLinesAndWriteLinesRoundTrip()
        {
            string path = Path.Combine(NewRoot(), "doorstop_config.ini");
            WriteFixture(path, new UTF8Encoding(true),
                         "[UnityDoorstop]\r\nenabled=true\r\n\r\ntargetAssembly=old.dll\r\n");

            string[] lines;
            using (FileStream stream = Open(path)) lines = Bootstrap.ReadLines(stream);

            SameLines("ReadLines returns every line, blank ones included", lines,
                      "[UnityDoorstop]", "enabled=true", "", "targetAssembly=old.dll");
            False("ReadLines strips the byte order mark rather than reading it as text",
                  lines.Length > 0 && lines[0].Length > 0 && lines[0][0] == '\uFEFF');

            using (FileStream stream = Open(path)) Bootstrap.WriteLines(stream, lines);

            byte[] written = File.ReadAllBytes(path);
            False("WriteLines writes no byte order mark", StartsWithBom(written));
            Same("WriteLines separates lines with the platform's newline and ends the file with one",
                 Encoding.UTF8.GetString(written),
                 "[UnityDoorstop]" + NL + "enabled=true" + NL + NL + "targetAssembly=old.dll" + NL);

            // A comment or a path in the patch's file can carry anything: UTF-8 in, UTF-8 out. The
            // literal is escaped so this source file stays ASCII and cannot depend on how a compiler
            // guesses its encoding.
            const string NonAscii = "# r\u00fcckw\u00e4rts \u2014 \u00fcn\u00efc\u00f6d\u00e9";

            path = Path.Combine(NewRoot(), "doorstop_config.ini");
            WriteFixture(path, new UTF8Encoding(false), NonAscii + "\ntargetAssembly=old.dll\n");

            using (FileStream stream = Open(path)) lines = Bootstrap.ReadLines(stream);
            SameLines("ReadLines reads UTF-8 with no byte order mark, and splits on a bare line feed",
                      lines, NonAscii, "targetAssembly=old.dll");

            using (FileStream stream = Open(path)) Bootstrap.WriteLines(stream, lines);
            using (FileStream stream = Open(path)) lines = Bootstrap.ReadLines(stream);
            SameLines("a round trip through both leaves the text unchanged", lines,
                      NonAscii, "targetAssembly=old.dll");
        }

        /// <summary>
        /// The ordering WriteLines documents: content down first, truncate second. Shrinking is the
        /// normal case rather than an edge one -- the displaced target is usually the longer
        /// string -- and it is the only one that proves the truncation happens at all.
        /// </summary>
        private static void WriteLinesTruncatesInBothDirections()
        {
            const string Displaced = @"ModSettings\a\long\path\somebody\else\put\here\ModSettings.dll";

            string path = Path.Combine(NewRoot(), "doorstop_config.ini");
            WriteFixture(path, new UTF8Encoding(false), "targetAssembly=" + Displaced + "\r\nenabled=true\r\n");
            Rewrite(path);

            string text = File.ReadAllText(path);
            Same("the shorter rewrite is the whole file", text,
                 "targetAssembly=" + Target + NL + "enabled=true" + NL);
            False("no fragment of the longer target survives the rewrite", text.Contains("ModSettings"));

            // And the other way, because a target shorter than ours grows the file instead.
            path = Path.Combine(NewRoot(), "doorstop_config.ini");
            WriteFixture(path, new UTF8Encoding(false), "targetAssembly=x.dll\r\n");
            Rewrite(path);

            Same("a longer rewrite lands whole", File.ReadAllText(path), "targetAssembly=" + Target + NL);
        }

        /// <summary>Read, retarget and write back over one handle, as EnsureDoorstopTarget does.</summary>
        private static void Rewrite(string path)
        {
            string[] lines;
            using (FileStream stream = Open(path)) lines = Bootstrap.ReadLines(stream);
            Bootstrap.Retarget(lines);
            using (FileStream stream = Open(path)) Bootstrap.WriteLines(stream, lines);
        }

        // ---- EnsureDoorstopTarget -----------------------------------------------------------------

        private static void EnsureDoorstopTargetRepairsARealFile()
        {
            string root = NewRoot();
            string ini = Path.Combine(root, "doorstop_config.ini");
            WriteFixture(ini, new UTF8Encoding(false),
                         "# Doorstop config" + NL + "[UnityDoorstop]" + NL + "enabled=true" + NL
                       + @"targetAssembly=ModSettings\ModSettings.dll" + NL + "dllSearchPathOverride=" + NL);

            Bootstrap.EnsureDoorstopTarget(root);

            Same("EnsureDoorstopTarget repairs the target and leaves every other line alone",
                 File.ReadAllText(ini),
                 "# Doorstop config" + NL + "[UnityDoorstop]" + NL + "enabled=true" + NL
               + "targetAssembly=" + Target + NL + "dllSearchPathOverride=" + NL);
            False("the repaired file carries no byte order mark", StartsWithBom(File.ReadAllBytes(ini)));

            // Two launches in a row. The second has nothing to do, and doing nothing is what keeps
            // this out of the way of a file the community patch owns.
            byte[] once = File.ReadAllBytes(ini);
            Bootstrap.EnsureDoorstopTarget(root);
            SameBytes("a second launch changes nothing", File.ReadAllBytes(ini), once);
        }

        private static void EnsureDoorstopTargetLeavesTheFileAlone()
        {
            // The fixtures below carry a byte order mark, bare line feeds and no final newline --
            // none of which WriteLines would reproduce. That is deliberate: if anything wrote these
            // files back, the bytes would say so.
            string root = NewRoot();
            string ini = Path.Combine(root, "doorstop_config.ini");
            WriteFixture(ini, new UTF8Encoding(true), "[UnityDoorstop]\nenabled=true");
            byte[] before = File.ReadAllBytes(ini);

            Bootstrap.EnsureDoorstopTarget(root);
            SameBytes("a file with no target key is not touched at all", File.ReadAllBytes(ini), before);

            root = NewRoot();
            ini = Path.Combine(root, "doorstop_config.ini");
            WriteFixture(ini, new UTF8Encoding(true), "targetAssembly=" + Target + "\nenabled=true");
            before = File.ReadAllBytes(ini);

            Bootstrap.EnsureDoorstopTarget(root);
            SameBytes("a file that already names us keeps its byte order mark and its line ends",
                      File.ReadAllBytes(ini), before);

            // No ini at all: a launch option install, or a folder that is not a game folder.
            // Creating the file would be inventing a format nobody asked for.
            root = NewRoot();
            Bootstrap.EnsureDoorstopTarget(root);
            False("EnsureDoorstopTarget creates no ini where there was none",
                  File.Exists(Path.Combine(root, "doorstop_config.ini")));
        }

        /// <summary>
        /// The second writer the exclusive handle exists for: the patch updater this bootstrap
        /// invoked a moment earlier, or the old ModSettings build re-asserting itself. Holding the
        /// file the same way proves the retry gives up quietly rather than throwing, and -- the part
        /// a player would notice -- that it gives up fast. This runs before Unity starts, so the
        /// wait is three attempts and two 50ms sleeps by design.
        /// </summary>
        private static void EnsureDoorstopTargetGivesUpOnALockedFile()
        {
            string root = NewRoot();
            string ini = Path.Combine(root, "doorstop_config.ini");
            WriteFixture(ini, new UTF8Encoding(false), "targetAssembly=old.dll" + NL);
            byte[] before = File.ReadAllBytes(ini);

            Stopwatch clock = Stopwatch.StartNew();
            using (new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Bootstrap.EnsureDoorstopTarget(root);
            }
            clock.Stop();

            SameBytes("a locked file is left exactly as it was", File.ReadAllBytes(ini), before);
            True("the retry gives up rather than delaying the launch (took " + clock.ElapsedMilliseconds + "ms)",
                 clock.ElapsedMilliseconds < 2000);

            // The launch after the one that lost the race. The repair is a fallback that can wait,
            // which is the whole reason giving up is safe.
            Bootstrap.EnsureDoorstopTarget(root);
            Same("the next launch repairs what the locked one could not",
                 File.ReadAllText(ini), "targetAssembly=" + Target + NL);
        }

        // ---- The command line ----------------------------------------------------------------------

        private static void CommandLineTargetIsRecognised()
        {
            // With this flag the ini cannot affect the launch, so writing to it would edit a file
            // the community patch owns to no effect whatsoever. All three prefixes and both
            // spellings count, because doorstop accepts all of them.
            foreach (string arg in new[] { "--doorstop-target", "-doorstop-target", "/doorstop-target",
                                           "--doorstop-target-assembly", "--doorstop_target_assembly",
                                           "--Doorstop-Target",
                                           @"--doorstop-target=C:\game\AS2ModLoader\AS2.Bootstrap.dll" })
            {
                True("'" + arg + "' counts as targeted on the command line",
                     Bootstrap.TargetedOnCommandLine(new[] { @"C:\game\Audiosurf2.exe", arg, @"C:\game\x.dll" }));
            }

            // An ini install, which is the install this repair is for. Nothing here may be mistaken
            // for the target flag: doorstop has other flags of its own, and reading one of them as
            // the target would switch the repair off for good.
            foreach (string arg in new[] { "-screen-fullscreen", "--doorstop-enabled", "--doorstop-dll-search-path",
                                           "--target", "targetAssembly", "", "   " })
            {
                False("'" + arg + "' is not a doorstop target flag",
                      Bootstrap.TargetedOnCommandLine(new[] { @"C:\game\Audiosurf2.exe", arg }));
            }

            False("an empty argument list is not targeted", Bootstrap.TargetedOnCommandLine(new string[0]));
            False("a null argument list is not targeted", Bootstrap.TargetedOnCommandLine(null));
            False("a null argument is skipped rather than dereferenced",
                  Bootstrap.TargetedOnCommandLine(new[] { null, @"C:\game\Audiosurf2.exe" }));
        }

        // ---- Fixtures and helpers --------------------------------------------------------------------

        /// <summary>A game folder of its own for each check, so none of them can see another's file.</summary>
        private static string NewRoot()
        {
            string dir = Path.Combine(_stem, "root" + (++_roots));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>The handle EnsureDoorstopTarget opens, opened the same way.</summary>
        private static FileStream Open(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        /// <summary>
        /// Writes a fixture with exactly the bytes asked for, byte order mark included or omitted as
        /// the encoding says. File.WriteAllLines would choose the line ends, and what happens to the
        /// line ends is half of what these checks are about.
        /// </summary>
        private static void WriteFixture(string path, Encoding encoding, string text)
        {
            File.WriteAllText(path, text, encoding);
        }

        private static bool StartsWithBom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static void SameLines(string what, string[] actual, params string[] expected)
        {
            Same(what, Join(actual), Join(expected));
        }

        /// <summary>Lines as one printable string, so a failure shows which line differs.</summary>
        private static string Join(string[] lines)
        {
            return lines == null ? "<null>" : string.Join(" | ", lines);
        }

        /// <summary>
        /// Byte for byte, for the checks that assert a file was not written. Text comparison would
        /// pass over exactly the differences those checks look for -- a byte order mark, a line end,
        /// a missing final newline.
        /// </summary>
        private static void SameBytes(string what, byte[] actual, byte[] expected)
        {
            if (actual.Length != expected.Length)
            {
                Fail(what + " -- expected " + expected.Length + " bytes, got " + actual.Length);
                return;
            }

            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] == expected[i]) continue;
                Fail(what + " -- byte " + i + " differs: expected " + expected[i] + ", got " + actual[i]);
                return;
            }

            Pass(what);
        }
    }
}
