using System;
using System.IO;
using BepInEx;

namespace AS2.ModApi
{
    // Storage layout
    // ===========================================================================================
    // BepInEx\config\ -- plugin configuration, BepInEx's own, use ConfigEntry<T>
    // BepInEx\data\   -- content data, this class
    // BepInEx 5 defines nothing for the second one, so this is the AS2 convention
    // Config is the knobs that configure the mod. Data is what it keeps about game content, keyed
    // by content and often not .cfg shaped
    // One shared folder, not a folder per mod: keeps the game directory clean, and keeps what a
    // player might back up in one place
    // Why it is split: see the AS2Paths wiki page

    /// <summary>Where an Audiosurf 2 mod puts its files</summary>
    public static class AS2Paths
    {
        /// <summary>
        /// Shared folder for AS2 mods' content data. Created on first use, so callers can simply
        /// write to whatever <see cref="DataFile"/> returns.
        /// </summary>
        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "data");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch (Exception e) { ModApiPlugin.Log.LogError("Could not create " + dir + ": " + e); }
                return dir;
            }
        }

        /// <summary>A file in the shared data folder. Name it after the mod, not the data</summary>
        // "skin-settings.json", not "settings.json" -- the folder is shared
        // Throws on anything but a plain file name, on purpose
        // A storage key comes from a Workshop folder name somebody else chose
        // Path.Combine takes a rooted name whole and drops the data folder
        // "nul.json" is a Windows device that swallows the write (see <see cref="PathGuard"/>)
        // Key data by putting the key inside one file, not by spelling it into the name
        public static string DataFile(string fileName)
        {
            if (Str.IsBlank(fileName)) throw new ArgumentException("A data file needs a name.", "fileName");

            if (!PathGuard.IsPlainFileName(fileName))
                throw new ArgumentException(
                    "A data file needs a plain file name -- not a path, and not a Windows device "
                    + "name such as NUL: '" + fileName + "'.", "fileName");

            return Path.Combine(DataDir, fileName);
        }

        /// <summary>Moves a file a mod used to keep elsewhere into the shared data folder, once</summary>
        // Does nothing if the destination exists, so it cannot clobber newer data
        // Never throws. A failed migration is a log line, not a broken mod
        // Returns true only when a file actually moved
        public static bool MigrateDataFile(string legacyPath, string fileName)
        {
            try
            {
                if (Str.IsBlank(legacyPath) || !File.Exists(legacyPath)) return false;

                string destination = DataFile(fileName);
                if (File.Exists(destination)) return false;

                File.Move(legacyPath, destination);
                ModApiPlugin.Log.LogInfo("Moved " + legacyPath + " to " + destination + ".");
                return true;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not migrate '" + legacyPath + "': " + e.Message);
                return false;
            }
        }

        /// <summary>Removes a now-empty folder a mod used to own. Best-effort and silent</summary>
        public static void RemoveIfEmpty(string directory)
        {
            try
            {
                if (Str.IsBlank(directory) || !Directory.Exists(directory)) return;
                if (Directory.GetFileSystemEntries(directory).Length > 0) return;
                Directory.Delete(directory);
                ModApiPlugin.Log.LogInfo("Removed the now-empty " + directory + ".");
            }
            catch { /* leaving a stray empty folder behind is not worth reporting */ }
        }
    }
}
