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
        /// somewhere the player chose, so there is a fallback: <see cref="RenameIntoPlace"/>. The
        /// fallback ends with the same two files File.Replace would have left, and gets there
        /// without needing <c>.prev</c> to be writable -- see the note there for why that matters.
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

                    return RenameIntoPlace(path, temp, previous);
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
        /// The fallback swap, for the filesystems where File.Replace does not work.
        ///
        /// <para>
        /// This used to delete the old file and then rename the new one over it. Two unguarded steps
        /// are one step too many: if the delete won and the rename then lost -- an anti-virus scanner
        /// holding the temp file for a moment, a sharing violation, a full disk -- the old contents
        /// were already gone, and the caller's cleanup deleted the temp file as well. Both copies of
        /// the player's data went, and the method reported an ordinary failed write.
        /// </para>
        ///
        /// <para>
        /// So the old file moves aside instead of going away. Every step after that has somewhere to
        /// go back to: the rename fails, the old file comes back, and the write is a plain failure
        /// again.
        /// </para>
        ///
        /// <para>
        /// It moves aside to <c>.bak</c> and not to <c>.prev</c>, which is the name File.Replace
        /// keeps its own backup under. Reusing <c>.prev</c> reads as tidy and is a trap: an
        /// unwritable <c>.prev</c> is one of the things that makes File.Replace fail in the first
        /// place, so a fallback that needs the same name inherits the very failure it is here to
        /// answer. A stale read-only copy, or a backup tool holding it open, took out both paths at
        /// once and the write failed with nowhere left to go. <c>.bak</c> is this method's own
        /// working name -- nothing else writes it, and it exists only between the two renames below.
        /// </para>
        ///
        /// <para>
        /// Once the swap is done, <c>.bak</c> becomes <c>.prev</c>, so a fallback write leaves the
        /// same two files a File.Replace write leaves. That step is housekeeping and is allowed to
        /// fail: by then the player's new contents are already in place, and nothing after this
        /// point may put them at risk to tidy a backup.
        /// </para>
        ///
        /// <para>Throws on failure, which the caller logs. Returns true only once the swap is done.</para>
        /// </summary>
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

        /// <summary>
        /// Puts the old contents back after the fallback swap failed halfway.
        ///
        /// If even this cannot run, the file is still on disk under the <c>.bak</c> name and the
        /// player can rename it by hand -- so the log line has to say so. That is the whole reason
        /// the old file is moved rather than deleted: there is always something left to name.
        /// </summary>
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

        /// <summary>
        /// Files the superseded copy under the name File.Replace would have used, or drops it when
        /// that name cannot be written -- which is often exactly why the fallback ran.
        ///
        /// Neither outcome can fail the write: it already succeeded. What must not happen is a
        /// <c>.bak</c> left in <c>BepInEx\data\</c>, because the folder the player is told to back
        /// up would then fill with copies of every save that ever took this path.
        /// </summary>
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
