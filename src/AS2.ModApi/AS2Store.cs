using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AS2.ModApi
{
    /// <summary>Reading and writing a mod's own data file without losing it</summary>
    // <see cref="AS2Paths"/> says where a file goes. This says how to put it there
    // Three mods keep player data in BepInEx\data\, all three want the same three things, and all
    // three would otherwise write the same subtly-wrong version of them
    // Text, not JSON, on purpose. A serialiser in the shared API means every mod inherits its
    // version and its settings
    // What is hard here is not turning an object into a string. It is the two file operations
    // below, which are easy to write and easy to write wrongly
    // No game type and no Unity type, so the cold checks cover it
    public static class AS2Store
    {
        /// <summary>How many quarantined copies of one file to keep before the oldest is dropped</summary>
        private const int MaxQuarantined = 5;

        /// <summary>Guards <see cref="Padlocks"/>, and nothing else</summary>
        private static readonly object Gate = new object();

        /// <summary>One padlock per file this process has touched, held for the life of it</summary>
        // #59. Every other piece of shared mutable state here is guarded -- ModMenuRegistry (#16),
        // MessengerBridge.Claim (#46), AS2GameEvents' add and remove (#26) -- and this was not,
        // though it is the one holding what a player would actually miss
        // The temp and backup names are derived from the path, so two threads writing one path
        // write one .tmp. The loser gets a sharing violation; the winner's swap consumes the file
        // the loser still means to swap, and the fallback then moves the correctly-written file
        // aside, fails on a temp file that is gone, puts it back, and reports a failed write for
        // data that is on disk. The racing check in tests/ saw that on every round, and saw the
        // file left holding bytes no caller ever wrote on a third of them
        // A padlock for each path rather than one for the store: two mods saving at once is
        // ordinary and shares nothing, and one lock over all of them would serialise a slow write
        // on a network share in front of every other mod's
        //
        // Per path, and per process. Two copies of the game running out of one folder still race,
        // and no lock reachable from here can answer that -- it would need the filesystem's
        //
        // Nothing is ever removed. An entry is one small object against a path string, the paths
        // are data file names rather than anything a player can multiply, and counting entries
        // back out again would put a second race where this one was
        private static readonly Dictionary<string, object> Padlocks =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The padlock every operation on one file takes, the same one for every caller</summary>
        // Keyed on the full path, because two callers naming one file differently -- a relative
        // path, a doubled separator, the other casing -- must not each get their own
        // GetFullPath throws on a path Win32 will not parse. The raw string is the honest fallback:
        // it still gives identical spellings one padlock, and a path that malformed is about to
        // fail the write anyway
        private static object PadlockFor(string path)
        {
            string key;
            try { key = Path.GetFullPath(path); }
            catch { key = path; }

            lock (Gate)
            {
                object padlock;
                if (!Padlocks.TryGetValue(key, out padlock))
                {
                    padlock = new object();
                    Padlocks[key] = padlock;
                }
                return padlock;
            }
        }

        /// <summary>Reads a file, or returns null when it is not there</summary>
        // Absent and unreadable are both null on purpose
        // A mod's first run has no file, and a mod that has to tell the two apart is a mod about to
        // lose the player's data over a transient sharing violation
        // Treat null as "start empty". Never as "the player has no data, so overwrite"
        public static string Read(string path)
        {
            if (Str.IsBlank(path)) return null;

            try
            {
                // Under the padlock: File.Replace unlinks and relinks the name, and a read landing
                // in that window comes back a sharing violation -- which this reports as null, and
                // null means "start empty" to every caller. Between processes that is ordinary and
                // harmless. Inside one, it is a read of the player's data answering "there is none"
                // while another thread of the same game is midway through saving it.
                lock (PadlockFor(path))
                {
                    if (!File.Exists(path)) return null;
                    return File.ReadAllText(path);
                }
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not read " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Writes a file so a crash cannot leave a half-written one behind</summary>
        // Open the real file and write, and there is a window where it is truncated and the new
        // contents are not on disk yet. A crash, a power cut or an Alt-F4 in it costs the player
        // everything the file held
        // So: write a sibling .tmp, then swap it into place. The swap is the part that must be atomic
        // File.Replace is preferred, because it also keeps a .prev copy of what was there
        // It fails across volumes and on some network paths, and the game folder is somewhere the
        // player chose, so <see cref="RenameIntoPlace"/> is the fallback
        // Returns false rather than throwing. Losing a save is a log line, not a crash
        public static bool WriteAtomic(string path, string contents)
        {
            if (Str.IsBlank(path)) return false;

            string temp = path + ".tmp";
            string previous = path + ".prev";

            try
            {
                // The temp write and the swap are one operation or they are nothing: both names
                // above are derived from the path, so a second thread on the same path is writing
                // the same .tmp. See PadlockFor.
                lock (PadlockFor(path))
                {
                    return WriteLocked(path, temp, previous, contents);
                }
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogError("Could not write " + path + ": " + e);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* nothing left to try */ }
                return false;
            }
        }

        /// <summary>The write itself, with the path's padlock already held</summary>
        // Split out so the lock is one statement rather than a brace around the whole body, and so
        // the failure path above stays the single place a failed write is reported
        private static bool WriteLocked(string path, string temp, string previous, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            if (!Str.IsBlank(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(temp, contents ?? string.Empty);

            if (!File.Exists(path))
            {
                // Nothing to replace, so the rename is the whole operation and is already atomic.
                File.Move(temp, path);
                return true;
            }

            try
            {
                File.Replace(temp, path, previous, true);
                return true;
            }
            catch (Exception replaceFailed)
            {
                ModApiPlugin.Log.LogWarning(
                    "Atomic replace of " + path + " failed, falling back to a rename: "
                    + replaceFailed.Message);

                return RenameIntoPlace(path, temp, previous);
            }
        }

        /// <summary>The fallback swap, for the filesystems where File.Replace does not work</summary>
        // #32 -- this used to delete the old file, then rename the new one over it
        // Two unguarded steps is one too many. The delete wins, the rename loses (an AV scanner
        // holding the temp file, a sharing violation, a full disk), the old contents are gone, and
        // the caller's cleanup deletes the temp file too
        // Both copies of the player's data went, reported as an ordinary failed write
        //
        // So the old file moves aside instead of going away, and every step after has somewhere to
        // go back to: the rename fails, the old file comes back, and it is a plain failure again
        //
        // Aside to .bak, not to .prev, which is File.Replace's own backup name
        // Reusing .prev reads as tidy and is a trap: an unwritable .prev is one of the things that
        // makes File.Replace fail, so a fallback needing the same name inherits the failure it is
        // here to answer
        // A stale read-only copy, or a backup tool holding it open, took out both paths at once
        // .bak is this method's own working name, written by nothing else, and it exists only
        // between the two renames below
        //
        // After the swap .bak becomes .prev, so a fallback write leaves the same two files a
        // File.Replace write leaves
        // That step is housekeeping and may fail. The player's new contents are already in place,
        // and nothing past this point may risk them to tidy a backup
        //
        // Throws on failure, which the caller logs. Returns true only once the swap is done
        private static bool RenameIntoPlace(string path, string temp, string previous)
        {
            string backup = path + ".bak";

            // Only an earlier fallback that could not clean up leaves one of these. Clearing it is
            // safe here and nowhere later: the real file is still in place, so a throw at this point
            // costs nothing but the write.
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(path, backup);

            try
            {
                File.Move(temp, path);
            }
            catch
            {
                Restore(backup, path);
                throw;
            }

            KeepAsPrevious(backup, previous);
            return true;
        }

        /// <summary>Puts the old contents back after the fallback swap failed halfway</summary>
        // If even this cannot run, the file is on disk under .bak and the player can rename it by
        // hand, so the log line has to say so
        // That is the whole reason the old file moves rather than being deleted -- there is always
        // something left to name
        private static void Restore(string backup, string path)
        {
            try
            {
                if (!File.Exists(path) && File.Exists(backup)) File.Move(backup, path);
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogError(
                    "Could not put " + path + " back after the write failed: " + e.Message
                    + " The previous contents are safe as " + backup
                    + "; rename that file to " + Path.GetFileName(path) + " to get them back.");
            }
        }

        /// <summary>Files the superseded copy under the name File.Replace would have used</summary>
        // Or drops it when that name cannot be written, which is often why the fallback ran
        // Neither outcome can fail the write -- it already succeeded
        // What must not happen is a .bak left in BepInEx\data\, or the folder the player is told
        // to back up fills with copies of every save that took this path
        private static void KeepAsPrevious(string backup, string previous)
        {
            try
            {
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(backup, previous);
                return;
            }
            catch { /* the name is not available; the copy is superseded anyway, so let it go */ }

            try { if (File.Exists(backup)) File.Delete(backup); }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Left a superseded copy at " + backup + ": " + e.Message);
            }
        }

        /// <summary>Moves a file that could not be parsed aside, so the mod can start fresh</summary>
        // The instinct on a corrupt data file is to overwrite it. Resist it
        // The file is the only copy of something the player spent time on, "corrupt" often means one
        // bad character in an otherwise complete file, and a mod that silently deletes it is a mod
        // nobody trusts twice
        // Renames to <name>.<timestamp>.bad and keeps 5, so a repeated failure cannot fill the folder
        //
        // The timestamp carries fractional seconds, which is not decoration
        // <see cref="TrimQuarantined"/> decides what to drop by sorting the names, so the names have
        // to sort into the order they were made
        // A whole-second stamp does not: several failures in one second collide, and a numeric
        // suffix to disambiguate makes it worse, because '-' sorts before '.' and the un-suffixed
        // name (the oldest of the group) ends up last
        // The first version did exactly that and trimmed the newest copy every time
        //
        // Returns the path it moved to, or null when there was nothing to move
        public static string Quarantine(string path)
        {
            try
            {
                if (Str.IsBlank(path)) return null;

                // The same padlock the write takes. This renames the very file WriteAtomic tests
                // for and then swaps over, so a quarantine landing inside a write turns that swap
                // into a create -- and the copy set aside is the one the write was replacing.
                lock (PadlockFor(path))
                {
                    if (!File.Exists(path)) return null;

                    string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
                    string destination = path + "." + stamp + ".bad";

                    // Two failures inside the same tenth of a microsecond are not going to happen, but a
                    // collision must not throw over the file it was trying to save. The suffix keeps the
                    // same width so it cannot disturb the ordering above.
                    int suffix = 1;
                    while (File.Exists(destination))
                    {
                        destination = path + "." + stamp + suffix.ToString("D2", CultureInfo.InvariantCulture) + ".bad";
                        suffix++;
                }

                File.Move(path, destination);
                ModApiPlugin.Log.LogWarning("Kept the unreadable " + path + " as " + destination + ".");

                TrimQuarantined(path);
                return destination;
                }
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not set aside " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Drops the oldest quarantined copies past <see cref="MaxQuarantined"/></summary>
        private static void TrimQuarantined(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (Str.IsBlank(directory)) return;

                string pattern = Path.GetFileName(path) + ".*.bad";
                string[] found = Directory.GetFiles(directory, pattern);
                if (found.Length <= MaxQuarantined) return;

                // The timestamp is in the name and sorts chronologically, so the name is the order.
                // File times are not: a copy restored from a backup carries the wrong one. See the
                // note on Quarantine for why the stamp needs its fractional part to make this true.
                List<string> ordered = new List<string>(found);
                ordered.Sort(StringComparer.Ordinal);

                for (int i = 0; i < ordered.Count - MaxQuarantined; i++)
                {
                    try { File.Delete(ordered[i]); }
                    catch { /* a leftover copy is better than a throw during cleanup */ }
                }
            }
            catch { /* trimming is housekeeping; never let it break a save */ }
        }
    }
}
