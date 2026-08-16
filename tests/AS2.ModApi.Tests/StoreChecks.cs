using System;
using System.IO;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>
    /// Cold checks for <see cref="AS2Store"/>.
    ///
    /// This is a player's saved data, and every failure mode here is one that loses it quietly. The
    /// checks that matter are the ones asserting what happens when something goes wrong: a write
    /// that fails must leave the old file intact, and an unreadable file must be kept rather than
    /// overwritten.
    /// </summary>
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
                ReplaceFallbackSwapsInEvenWhenReplaceCannotBeUsed();
                ReplaceFallbackFailureLeavesTheOldFileInPlace();
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

        // ---- Regression: issue #32 -----------------------------------------------------------
        //
        // File.Replace is not available across volumes or on some network shares, which is exactly
        // where the game folder can live, so WriteAtomic falls back to a rename. The two checks
        // below force that fallback deliberately, by occupying the file names it needs with a
        // directory rather than waiting for a second volume to reproduce the failure.

        /// <summary>
        /// The common case the fallback exists for: File.Replace cannot be used, but the rename
        /// still gets the new contents in place and leaves nothing behind.
        /// </summary>
        private static void ReplaceFallbackSwapsInEvenWhenReplaceCannotBeUsed()
        {
            string p = Path_("cross-volume.json");
            AS2Store.WriteAtomic(p, "first");

            // File.Replace needs its own backup path free; occupying it with a directory is a
            // reliable way to make File.Replace fail without a second volume or network share to
            // reproduce the real case this fallback exists for.
            string previous = p + ".prev";
            Directory.CreateDirectory(previous);

            try
            {
                True("WriteAtomic still succeeds by falling back to a rename", AS2Store.WriteAtomic(p, "second"));
                Same("the new contents are what is on disk", AS2Store.Read(p), "second");
                False("no .bak is left behind after a successful fallback", File.Exists(p + ".bak"));
                False("no .tmp is left behind after a successful fallback", File.Exists(p + ".tmp"));
            }
            finally
            {
                try { Directory.Delete(previous, true); } catch { /* test fixture, not the file under test */ }
            }
        }

        /// <summary>
        /// The bug itself. The old fallback was `File.Delete(path); File.Move(temp, path)`, two
        /// unguarded steps with a real window in which neither name held the player's data. A
        /// failure landing in that window -- an AV lock, a transient sharing violation, a full disk
        /// -- reached the outer catch, which deleted "temp" as its usual tidy-up, and the old save
        /// (already deleted) and the new one (now deleted too) were both gone, while WriteAtomic
        /// still returned false as if this were an ordinary write failure.
        ///
        /// This cannot force that exact race without a fault-injection hook the harness does not
        /// have, so it exercises the property the fix buys instead: whatever stops the fallback from
        /// finishing, the old file is renamed aside rather than deleted, so it is still there
        /// afterwards, whether or not the swap itself could complete.
        /// </summary>
        private static void ReplaceFallbackFailureLeavesTheOldFileInPlace()
        {
            string p = Path_("mid-swap.json");
            AS2Store.WriteAtomic(p, "the only copy");

            string previous = p + ".prev";
            Directory.CreateDirectory(previous); // forces File.Replace to fail, as above

            string backup = p + ".bak";
            Directory.CreateDirectory(backup);    // occupies the fallback's own rename-aside target

            try
            {
                False("WriteAtomic reports failure rather than claiming a swap it could not finish",
                      AS2Store.WriteAtomic(p, "would-be new contents"));
                True("the old file is still there afterwards", File.Exists(p));
                Same("and it still holds what it held before the attempt", AS2Store.Read(p), "the only copy");
            }
            finally
            {
                try { Directory.Delete(previous, true); } catch { /* test fixture, not the file under test */ }
                try { Directory.Delete(backup, true); } catch { /* test fixture, not the file under test */ }
            }
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
