using System;
using System.IO;
using BepInEx;

namespace AS2.ModApi
{
    /// <summary>
    /// Where an Audiosurf 2 mod should put its files.
    ///
    /// BepInEx gives every plugin `BepInEx\config\` for its **own** settings -- the knobs that
    /// configure the mod itself, in BepInEx's `.cfg` format. That is not the same thing as data a
    /// mod keeps *about game content*: which palette the player picked for one skin, a per-mode
    /// score history, notes attached to a song. Those are keyed by content rather than by the mod,
    /// they are often not `.cfg`-shaped, and a player clearing plugin config to fix a misbehaving
    /// mod should not lose them.
    ///
    /// BepInEx 5 defines no location for that, so this is the convention for Audiosurf 2:
    ///
    ///     BepInEx\config\   plugin configuration   (BepInEx's own, use ConfigEntry&lt;T&gt;)
    ///     BepInEx\data\     content data           (this class)
    ///
    /// Using one shared folder rather than a folder per mod keeps the game directory clean and
    /// keeps everything a player might want to back up or copy between installs in one place.
    /// </summary>
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

        /// <summary>
        /// A file in the shared data folder. Name it after the mod, not after the data
        /// ("skin-settings.json", not "settings.json"), because the folder is shared.
        /// </summary>
        public static string DataFile(string fileName)
        {
            if (Str.IsBlank(fileName)) throw new ArgumentException("A data file needs a name.", "fileName");
            return Path.Combine(DataDir, fileName);
        }

        /// <summary>
        /// Moves a file a mod used to keep somewhere else into the shared data folder, once.
        ///
        /// Does nothing if the destination already exists, so it cannot clobber newer data, and
        /// never throws: failing to migrate is worth a log line, not a broken mod. Returns true only
        /// when a file was actually moved.
        /// </summary>
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

        /// <summary>Removes a now-empty folder a mod used to own. Best-effort and silent.</summary>
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
