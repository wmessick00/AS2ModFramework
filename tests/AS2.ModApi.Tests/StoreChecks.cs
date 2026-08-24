using System;
using System.IO;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>Cold checks for <see cref="AS2Store"/></summary>
    // A player's saved data, and every failure mode here loses it quietly
    // The checks that matter assert what happens when something goes wrong:
    //   -a write that fails leaves the old file intact
    //   -an unreadable file is kept, not overwritten
    internal static class StoreChecks
    {
        private static string _dir;

        internal static void Run()
        {
            _dir = Path.Combine(Path.GetTempPath(), "as2-store-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);

            try
            {
                ReadIsNullWhenThereIsNoFile();
                WriteThenReadRoundTrips();
                WriteOverExistingKeepsTheContent();
                WriteLeavesNoTempBehind();
                WriteCreatesAMissingDirectory();
                TheFallbackSwapRunsWhenReplaceCannot();
                AFailedFallbackSwapKeepsTheOldContents();
                QuarantineMovesRatherThanDeletes();
                QuarantineIsNullWhenThereIsNothingToMove();
                QuarantineKeepsOnlyTheMostRecentFew();
            }
            finally
            {
                try { Directory.Delete(_dir, true); } catch { /* a temp folder left behind is not a failure */ }
            }
        }

        private static string Path_(string name) { return Path.Combine(_dir, name); }

        private static void ReadIsNullWhenThereIsNoFile()
        {
            Null("Read returns null for a file that is not there", AS2Store.Read(Path_("absent.json")));
            Null("Read returns null for a blank path", AS2Store.Read(""));
        }

        private static void WriteThenReadRoundTrips()
        {
            string p = Path_("round-trip.json");
            True("WriteAtomic reports success", AS2Store.WriteAtomic(p, "{\"a\":1}"));
            Same("the contents survive the swap", AS2Store.Read(p), "{\"a\":1}");
        }

        /// <summary>
        /// The case the atomic swap exists for. A second write must end with the new contents in
        /// place and nothing half-written, which is what File.Replace buys over open-and-truncate.
        /// </summary>
        private static void WriteOverExistingKeepsTheContent()
        {
            string p = Path_("overwrite.json");
            AS2Store.WriteAtomic(p, "first");
            True("the second write succeeds", AS2Store.WriteAtomic(p, "second"));
            Same("the second write is what is on disk", AS2Store.Read(p), "second");
        }

        /// <summary>
        /// A stray .tmp is not cosmetic. It is the same size as the real file, it sits in a folder
        /// the player is told to back up, and a later reader that globs the folder will find it.
        /// </summary>
        private static void WriteLeavesNoTempBehind()
        {
            string p = Path_("tidy.json");
            AS2Store.WriteAtomic(p, "one");
            AS2Store.WriteAtomic(p, "two");
            False("no .tmp is left after a write", File.Exists(p + ".tmp"));
        }

        private static void WriteCreatesAMissingDirectory()
        {
            string p = Path.Combine(_dir, Path.Combine("nested", "deep.json"));
            True("WriteAtomic creates the folder it needs", AS2Store.WriteAtomic(p, "x"));
            Same("and the file is readable afterwards", AS2Store.Read(p), "x");
        }

        // ---- Regression: issue #32 -------------------------------------------------------------

        /// <summary>The fallback swap doing its job: File.Replace cannot run, the write still lands</summary>
        // Occupying .prev with a directory is what makes File.Replace fail here
        // A stand-in for the cross-volume and network-share cases the fallback really exists for,
        // which no test can arrange on one machine
        // It is also the honest shape of the hazard: an unwritable .prev is one of the things that
        // makes File.Replace fail in the field
        // The first fix for #32 moved the old file aside to .prev, the name already established as
        // unavailable, so the fallback failed for the same reason the primary path had
        // Hence .bak, and hence this check, which fails against that first fix
        // The technique comes from PR #34, which reached the same fix independently
        private static void TheFallbackSwapRunsWhenReplaceCannot()
        {
            string p = Path_("no-replace.json");
            AS2Store.WriteAtomic(p, "first");

            string previous = p + ".prev";
            try { if (File.Exists(previous)) File.Delete(previous); } catch { }
            Directory.CreateDirectory(previous);

            try
            {
                ModApiPlugin.Log.Clear();
                True("WriteAtomic still succeeds when File.Replace cannot run",
                     AS2Store.WriteAtomic(p, "second"));
                True("and it really did take the fallback", ModApiPlugin.Log.Mentions("falling back to a rename"));
                Same("the new contents are what is on disk", AS2Store.Read(p), "second");
                False("the fallback leaves no .bak behind", File.Exists(p + ".bak"));
                False("the fallback leaves no .tmp behind", File.Exists(p + ".tmp"));
            }
            finally
            {
                try { Directory.Delete(previous, true); } catch { /* the fixture's own leftover */ }
            }
        }

        /// <summary>The fallback swap failing in the middle, with the player's data on the line</summary>
        // The fallback used to delete the old file, then rename the new one over it
        // A failure between those two steps left nothing: the old file was gone, and the caller's
        // cleanup deleted the temp file holding the new contents
        // WriteAtomic reported an ordinary failed write, so the log said nothing about the loss
        // The fixture holds the temp file open, sharing read and write but not delete
        // Enough to make File.Replace fail, since it needs delete access on the file it moves in,
        // and then to make the fallback's own rename of that file fail too
        // Nothing holds the real file, so the step before it succeeds and the write gets halfway
        private static void AFailedFallbackSwapKeepsTheOldContents()
        {
            string p = Path_("mid-swap.json");
            AS2Store.WriteAtomic(p, "the player's data");

            string temp = p + ".tmp";
            File.WriteAllText(temp, "");

            bool wrote;
            ModApiPlugin.Log.Clear();
            using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                wrote = AS2Store.WriteAtomic(p, "the replacement");
            }

            if (ModApiPlugin.Log.Mentions("falling back to a rename"))
            {
                False("a write that could not finish reports failure", wrote);
                True("the old file is still in its own name", File.Exists(p));
                Same("and it still holds every byte the player had", AS2Store.Read(p), "the player's data");
            }
            else
            {
                Skip("The mid-swap failure did not reproduce: File.Replace got past a temp file held "
                   + "open without delete sharing, so the fallback never ran on this machine.");
            }

            try { File.Delete(temp); } catch { /* the fixture's own leftover, not the code's */ }
        }

        /// <summary>
        /// The whole point of Quarantine: a file that could not be parsed is still the player's, and
        /// deleting it to start clean is the behaviour this exists to prevent.
        /// </summary>
        private static void QuarantineMovesRatherThanDeletes()
        {
            string p = Path_("corrupt.json");
            File.WriteAllText(p, "{ this is not json");

            string moved = AS2Store.Quarantine(p);

            True("Quarantine reports where it put the file", !string.IsNullOrEmpty(moved));
            False("the original is gone from its own name", File.Exists(p));
            if (!string.IsNullOrEmpty(moved))
            {
                True("the quarantined copy exists", File.Exists(moved));
                Same("and it still holds every byte", File.ReadAllText(moved), "{ this is not json");
                True("the quarantined copy is named .bad", moved.EndsWith(".bad", StringComparison.Ordinal));
            }
        }

        private static void QuarantineIsNullWhenThereIsNothingToMove()
        {
            Null("Quarantine returns null when the file is not there", AS2Store.Quarantine(Path_("never-existed.json")));
        }

        /// <summary>
        /// A mod that fails to parse on every launch would otherwise fill BepInEx\data with copies.
        /// Six failures must leave five files, and the five must be the newest.
        /// </summary>
        private static void QuarantineKeepsOnlyTheMostRecentFew()
        {
            string p = Path_("repeat.json");

            for (int i = 0; i < 8; i++)
            {
                File.WriteAllText(p, "bad " + i);
                AS2Store.Quarantine(p);
            }

            string[] kept = Directory.GetFiles(_dir, "repeat.json.*.bad");
            True("repeated quarantine keeps at most five copies, kept " + kept.Length, kept.Length <= 5);

            bool newestSurvived = false;
            foreach (string f in kept)
                if (File.ReadAllText(f) == "bad 7") newestSurvived = true;
            True("the most recent failure is one of the copies kept", newestSurvived);
        }
    }
}
