using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AS2.ModApi
{
    /// <summary>
    /// Reading and writing a mod's own data file without losing it.
    ///
    /// <see cref="AS2Paths"/> says where a file goes. This says how to put it there. Three mods now
    /// keep player data in <c>BepInEx\data\</c>, all three want the same three things, and all three
    /// would otherwise write the same subtly-wrong version of them.
    ///
    /// <para>
    /// <b>Text, not JSON, and deliberately so.</b> The game ships Newtonsoft.Json and a mod is
    /// welcome to use it; this framework does not, because a serialiser in the shared API means
    /// every mod inherits its version and its settings. What is actually hard here is not turning an
    /// object into a string -- it is the two file operations below, which are easy to write and
    /// easy to write wrongly.
    /// </para>
    ///
    /// <para>
    /// This class touches no game type and no Unity type, so it is checked by the cold tests rather
    /// than by launching the game.
    /// </para>
    /// </summary>
    public static class AS2Store
    {
        /// <summary>How many quarantined copies of one file to keep before the oldest is dropped.</summary>
        private const int MaxQuarantined = 5;

        /// <summary>
        /// Reads a file, or returns null when it is not there.
        ///
        /// Absent and unreadable are both null on purpose: a mod's first run has no file, and a mod
        /// that has to tell the two apart is a mod about to lose the player's data over a transient
        /// sharing violation. Treat null as "start empty" and never as "the player has no data, so
        /// overwrite".
        /// </summary>
        public static string Read(string path)
        {
            try
            {
                if (Str.IsBlank(path) || !File.Exists(path)) return null;
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not read " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Writes a file so that a crash cannot leave a half-written one behind.
        ///
        /// <para>
        /// The naive version -- open the real file and write -- has a window in which the file is
        /// truncated and the new contents are not yet on disk. A crash, a power cut or an Alt-F4
        /// inside that window costs the player everything the file held. So this writes a sibling
        /// <c>.tmp</c> first and then swaps it into place, and the swap is the part that has to be
        /// atomic.
        /// </para>
        ///
        /// <para>
        /// File.Replace is that swap and is preferred, because it also keeps a <c>.prev</c> copy of
        /// what was there. It fails across volumes and on some network paths, and the game folder is
        /// somewhere the player chose, so there is a fallback: rename the previous save aside, move
        /// the new file into place, then drop the renamed-aside copy. That fallback has a real -- if
        /// tiny -- window where neither file is at <c>path</c>, which is why it is second. It never
        /// has a window where neither file exists anywhere: renaming aside rather than deleting means
        /// a failure on the move that follows still leaves both copies recoverable on disk under
        /// their own names, which a bare delete-then-move does not (see issue #32).
        /// </para>
        ///
        /// <para>Returns false rather than throwing. Losing a save is worth a log line, not a crash.</para>
        /// </summary>
        public static bool WriteAtomic(string path, string contents)
        {
            if (Str.IsBlank(path)) return false;

            string temp = path + ".tmp";
            string previous = path + ".prev";

            try
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

                    return FallbackRename(path, temp);
                }
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogError("Could not write " + path + ": " + e);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* nothing left to try */ }
                return false;
            }
        }

        /// <summary>
        /// The fallback swap used when File.Replace itself is not available. Delete-then-move was
        /// the original shape here and is wrong: it is two separate, unguarded operations, and a
        /// crash or a lock between them (an AV scanner, a transient sharing violation, a full disk)
        /// leaves the outer catch deleting <paramref name="temp"/> as "cleanup" with the previous
        /// save already gone -- both copies lost, reported as an ordinary write failure rather than
        /// what it is (see issue #32).
        ///
        /// <para>
        /// This renames <paramref name="path"/> aside instead of deleting it, moves the new content
        /// into place, and only then drops the renamed-aside copy -- restoring it if the move fails.
        /// Every step from here on either leaves both files present under their own names or leaves
        /// exactly the swap File.Replace itself would have made; nothing is deleted before its
        /// replacement is confirmed on disk.
        /// </para>
        /// </summary>
        private static bool FallbackRename(string path, string temp)
        {
            string backup = path + ".bak";

            // A stray .bak from an earlier, unrecovered failure of this same fallback would make the
            // rename below throw "file already exists" and abort a write that has nothing to do with
            // it. Clearing it first costs nothing: this call is about to make a fresher one anyway.
            try { if (File.Exists(backup)) File.Delete(backup); } catch { /* best effort */ }

            try
            {
                File.Move(path, backup);
            }
            catch (Exception renameAsideFailed)
            {
                ModApiPlugin.Log.LogError(
                    "Could not set aside " + path + " for the fallback rename: " + renameAsideFailed.Message);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* nothing left to try */ }
                return false;
            }

            try
            {
                MoveIntoPlace(temp, path);
            }
            catch (Exception moveFailed)
            {
                // The previous save is safely under backup's name; put it straight back rather than
                // leaving the player with nothing at path.
                try
                {
                    RestoreBackup(backup, path);
                    ModApiPlugin.Log.LogError(
                        "Could not move the new " + path + " into place, restored the previous save: "
                        + moveFailed.Message);
                    // The abandoned new content is redundant once the old file is safely back: keep
                    // no more than File.Replace itself would have left behind on an ordinary failure.
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { /* nothing left to try */ }
                }
                catch (Exception restoreFailed)
                {
                    // The case the rename-aside exists to survive: neither file could end up at path.
                    // Nothing is lost even here, only misplaced -- the previous save is intact at
                    // backup and the new contents are intact at temp, both under their own names, so
                    // this deliberately deletes neither.
                    ModApiPlugin.Log.LogError(
                        "Could not write " + path + " and could not restore the previous save either. "
                        + "The previous save is intact at " + backup + " and the new contents are intact "
                        + "at " + temp + ". Restore failure: " + restoreFailed.Message
                        + ". Original failure: " + moveFailed.Message);
                }
                return false;
            }

            try { File.Delete(backup); } catch { /* a stray .bak left behind is not a failure */ }
            return true;
        }

        /// <summary>
        /// Hooks for the two moves inside <see cref="FallbackRename"/> that put the new content in
        /// place and, failing that, restore the old. Production always uses the real
        /// <see cref="File.Move(string, string)"/>; nothing here changes unless a test overrides it.
        ///
        /// They exist because those two failures are the ones issue #32 is about, and neither can be
        /// made to happen on a real filesystem to order: the window between the rename-aside
        /// succeeding and the next move starting is real but microseconds wide, so a test cannot race
        /// it reliably. See StoreChecks.cs for how the cold tests use these.
        /// </summary>
        internal static Action<string, string> MoveIntoPlace = File.Move;

        /// <summary>See <see cref="MoveIntoPlace"/>.</summary>
        internal static Action<string, string> RestoreBackup = File.Move;

        /// <summary>
        /// Moves a file that could not be parsed aside, so the mod can start fresh without deleting
        /// whatever the player had.
        ///
        /// <para>
        /// The instinct on a corrupt data file is to overwrite it. Resist it. The file is the only
        /// copy of something the player spent time on, "corrupt" often means one bad character in an
        /// otherwise complete file, and a mod that silently deletes it is a mod nobody trusts twice.
        /// This renames it to <c>&lt;name&gt;.&lt;timestamp&gt;.bad</c> and keeps the most recent
        /// few, so a repeated failure cannot fill the folder either.
        /// </para>
        ///
        /// <para>
        /// The timestamp carries fractional seconds, which is not decoration. <see
        /// cref="TrimQuarantined"/> decides what to drop by sorting the names, so the names have to
        /// sort into the order they were made. A whole-second stamp does not: several failures in
        /// one second collide, and disambiguating them with a numeric suffix makes it worse, because
        /// '-' sorts before '.' and the un-suffixed name -- the oldest of the group -- ends up last.
        /// The first version of this did exactly that and trimmed the newest copy every time.
        /// </para>
        ///
        /// <para>Returns the path it was moved to, or null when there was nothing to move.</para>
        /// </summary>
        public static string Quarantine(string path)
        {
            try
            {
                if (Str.IsBlank(path) || !File.Exists(path)) return null;

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
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not set aside " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Drops the oldest quarantined copies past <see cref="MaxQuarantined"/>.</summary>
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
