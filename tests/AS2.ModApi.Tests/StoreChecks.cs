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
                QuarantineMovesRatherThanDeletes();
                QuarantineIsNullWhenThereIsNothingToMove();
                QuarantineKeepsOnlyTheMostRecentFew();

                FallbackSucceedsWhenReplaceCannot();
                FallbackLeavesTheOriginalIntactWhenSettingItAsideFails();
                FallbackRestoresThePreviousSaveWhenTheMoveIntoPlaceFails();
                FallbackLosesNothingWhenBothTheMoveAndTheRestoreFail();
            }
            finally
            {
                AS2Store.MoveIntoPlace = File.Move;
                AS2Store.RestoreBackup = File.Move;
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

        // ---- Regression: issue #32 -----------------------------------------------------------
        //
        // WriteAtomic's fallback used to be two unguarded calls -- File.Delete(path) then
        // File.Move(temp, path) -- so a failure on the second call after the first had already
        // succeeded lost both the previous save and the new one, and returned false as though it
        // were an ordinary write failure. FallbackRename replaced that with a rename-aside that is
        // recoverable at every step. The four checks below force each of its branches, including
        // the one a real filesystem cannot be made to fail to order -- see AS2Store.MoveIntoPlace.

        /// <summary>
        /// Forces File.Replace itself to fail by making its own backup name -- <c>path + ".prev"</c>
        /// -- an existing directory, which File.Replace cannot use and WriteAtomic never clears
        /// first (unlike the ".bak" name FallbackRename owns). This is what exercises
        /// FallbackRename at all: on a single local temp folder File.Replace almost always
        /// succeeds, so without this every check below would be dead code.
        /// </summary>
        private static void ForceReplaceToFail(string path)
        {
            Directory.CreateDirectory(path + ".prev");
        }

        /// <summary>FallbackRename's own success path, previously untested because nothing here made File.Replace fail.</summary>
        private static void FallbackSucceedsWhenReplaceCannot()
        {
            string p = Path_("fallback-ok.json");
            AS2Store.WriteAtomic(p, "first");
            ForceReplaceToFail(p);

            ModApiPlugin.Log.Clear();
            True("the fallback succeeds when File.Replace cannot", AS2Store.WriteAtomic(p, "second"));
            True("and says it fell back", ModApiPlugin.Log.Mentions("falling back to a rename"));
            Same("and the new content is what is on disk", AS2Store.Read(p), "second");
            False("no .bak is left behind", File.Exists(p + ".bak"));
            False("no .tmp is left behind", File.Exists(p + ".tmp"));

            try { Directory.Delete(p + ".prev", true); } catch { /* cleanup only */ }
        }

        /// <summary>
        /// The rename-aside itself can fail too -- here, because a stray ".bak" directory already
        /// occupies the name -- and when it does, WriteAtomic must not have touched the original at
        /// all: it is still the file it always was, under its own name.
        /// </summary>
        private static void FallbackLeavesTheOriginalIntactWhenSettingItAsideFails()
        {
            string p = Path_("fallback-aside-fails.json");
            AS2Store.WriteAtomic(p, "original");
            ForceReplaceToFail(p);

            // A directory rather than a file: File.Exists(backup) is false for it, so WriteAtomic's
            // own "clear a stray .bak" step will not remove it, and File.Move(path, backup) has
            // nowhere to put path.
            Directory.CreateDirectory(p + ".bak");

            ModApiPlugin.Log.Clear();
            False("WriteAtomic reports failure when it cannot set the original aside", AS2Store.WriteAtomic(p, "new"));
            True("and says why", ModApiPlugin.Log.Mentions("Could not set aside"));
            True("the original file is exactly where it was", File.Exists(p));
            Same("and still holds what it held before", AS2Store.Read(p), "original");
            False("no .tmp is left behind", File.Exists(p + ".tmp"));

            try { Directory.Delete(p + ".prev", true); } catch { /* cleanup only */ }
            try { Directory.Delete(p + ".bak", true); } catch { /* cleanup only */ }
        }

        /// <summary>
        /// The failure issue #32 is actually about: the rename-aside succeeds, and the move that
        /// puts the new content in place then fails. The unfixed code had already deleted the
        /// original at this point and had nothing left to restore; the fix must put it back.
        /// </summary>
        private static void FallbackRestoresThePreviousSaveWhenTheMoveIntoPlaceFails()
        {
            string p = Path_("fallback-move-fails.json");
            AS2Store.WriteAtomic(p, "the only copy that must survive");
            ForceReplaceToFail(p);

            AS2Store.MoveIntoPlace = delegate { throw new IOException("simulated: locked by an AV scanner"); };

            ModApiPlugin.Log.Clear();
            False("WriteAtomic reports failure when the move into place fails", AS2Store.WriteAtomic(p, "new content"));
            True("and says the previous save was restored", ModApiPlugin.Log.Mentions("restored the previous save"));

            True("the previous save is back at its own name -- not lost", File.Exists(p));
            Same("holding exactly what it held before", AS2Store.Read(p), "the only copy that must survive");
            False("no .bak is left behind once restored", File.Exists(p + ".bak"));
            False("no .tmp is left behind either", File.Exists(p + ".tmp"));

            AS2Store.MoveIntoPlace = File.Move;
            try { Directory.Delete(p + ".prev", true); } catch { /* cleanup only */ }
        }

        /// <summary>
        /// The worst case the rename-aside exists for: the move into place fails and the restore
        /// that follows fails too. Even here nothing is deleted -- the previous save and the new
        /// content are both still on disk, only under their own names rather than the file's real
        /// one, which is the whole difference between this and the bug it replaces.
        /// </summary>
        private static void FallbackLosesNothingWhenBothTheMoveAndTheRestoreFail()
        {
            string p = Path_("fallback-double-fails.json");
            AS2Store.WriteAtomic(p, "the previous save");
            ForceReplaceToFail(p);

            AS2Store.MoveIntoPlace = delegate { throw new IOException("simulated: move into place failed"); };
            AS2Store.RestoreBackup = delegate { throw new IOException("simulated: restore failed too"); };

            ModApiPlugin.Log.Clear();
            False("WriteAtomic reports failure", AS2Store.WriteAtomic(p, "the new content"));
            True("and says neither file could end up at path",
                 ModApiPlugin.Log.Mentions("could not restore the previous save"));

            False("path itself is empty -- this is the real, if tiny, window", File.Exists(p));
            True("but the previous save is intact under its backup name", File.Exists(p + ".bak"));
            Same("holding exactly what it held before", File.ReadAllText(p + ".bak"), "the previous save");
            True("and the new content is intact under its temp name", File.Exists(p + ".tmp"));
            Same("holding exactly what WriteAtomic was asked to save", File.ReadAllText(p + ".tmp"), "the new content");

            AS2Store.MoveIntoPlace = File.Move;
            AS2Store.RestoreBackup = File.Move;
            try { Directory.Delete(p + ".prev", true); } catch { /* cleanup only */ }
            try { File.Delete(p + ".bak"); } catch { /* cleanup only */ }
            try { File.Delete(p + ".tmp"); } catch { /* cleanup only */ }
        }
    }
}
