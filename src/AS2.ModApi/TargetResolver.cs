using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;

namespace AS2.ModApi
{
    /// <summary>A skin or mode folder on disk.</summary>
    public sealed class Target
    {
        /// <summary>Storage key: path relative to the game root, forward slashes, no leading slash.</summary>
        public string Key;

        /// <summary>Absolute path of the skin/mode folder.</summary>
        public string FolderPath;

        /// <summary>Folder name, suitable as a display title.</summary>
        public string Name;

        public SelectorKind Kind;

        public override string ToString() { return Key; }
    }

    /// <summary>
    /// Owns "where is the game" and "how do I name a skin or mode folder".
    ///
    /// Keys are the currency of the whole API: they are what the game's relative paths normalise
    /// into, and what mods should use to store per-skin or per-mode data. The folder shapes that
    /// must all key correctly (see RingDesignManager.FindSkins and ModeSelect.GetModFolders in the
    /// decompiled game):
    ///
    ///   skins/&lt;name&gt;                 plain local skin
    ///   skins/&lt;steamid&gt;/&lt;name&gt;      Steam Workshop item (container folder name is all digits)
    ///   mods/&lt;mode&gt;/skins/&lt;name&gt;     skin dedicated to one mode
    ///
    /// plus mods/&lt;name&gt; and mods/&lt;steamid&gt;/&lt;name&gt; for modes.
    /// </summary>
    public static class TargetResolver
    {
        public const string DefaultSchemaFileName = "modsettings.lua";

        private static readonly Regex WorkshopContainer = new Regex(@"^\d+$", RegexOptions.Compiled);

        /// <summary>
        /// Absolute path of the Audiosurf 2 install folder.
        ///
        /// BepInEx has already resolved this from the process path by the time any plugin loads, so
        /// there is nothing to derive: mods used to walk up from their own assembly location and
        /// fall back to the working directory, because they ran before Unity could be asked.
        /// </summary>
        public static string GameRoot { get { return Paths.GameRootPath; } }

        public static string SkinsDir { get { return Path.Combine(GameRoot, "skins"); } }

        public static string ModsDir { get { return Path.Combine(GameRoot, "mods"); } }

        /// <summary>
        /// Turns an absolute folder path into its storage key, or null if it is not under the game
        /// root.
        /// </summary>
        public static string KeyForFolder(string absoluteFolder)
        {
            if (Str.IsBlank(absoluteFolder)) return null;
            try
            {
                string full = Path.GetFullPath(absoluteFolder);
                string root = RootPrefix();
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return Normalize(full.Substring(root.Length));
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not build a key for '" + absoluteFolder + "': " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// The game root with a trailing separator, which is what a containment test has to compare
        /// against: without it a sibling install like "...\Audiosurf 2 Backup" starts with the root
        /// as a plain string and would pass.
        /// </summary>
        private static string RootPrefix()
        {
            string root = Path.GetFullPath(GameRoot);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;
            return root;
        }

        /// <summary>
        /// Normalises a game-relative path into a storage key. The game hands these out with a
        /// leading slash (RingDesignManager.selectedSkinRelativePath is e.g. "/skins/Rainbowdrive")
        /// and occasionally with backslashes or doubled separators.
        ///
        /// The casing is then resolved against the real folder on disk, because the game is not
        /// consistent about it: browsing the selector can yield "skins/rainbowdrive" for the same
        /// skin that reports "skins/Rainbowdrive" once a song is set up. Keys are the currency of
        /// this API -- they end up as keys in mods' saved JSON -- so two spellings of one skin is a
        /// bug waiting to strand somebody's settings. It survives today only because Windows paths
        /// and the one dictionary that matters are both case-insensitive.
        ///
        /// A path with "." or ".." segments is rejected rather than collapsed: a key names a folder
        /// inside the install, so anything that climbs is either the game behaving in a way we have
        /// never seen or somebody feeding the API a path it should not follow. Every caller already
        /// treats null as "no target".
        /// </summary>
        public static string Normalize(string relativePath)
        {
            if (Str.IsBlank(relativePath)) return null;
            string s = relativePath.Replace('\\', '/');
            while (s.Contains("//")) s = s.Replace("//", "/");
            s = s.Trim('/');
            if (s.Length == 0) return null;
            if (HasDotSegment(s))
            {
                ModApiPlugin.Log.LogWarning("Refusing the relative path '" + relativePath + "': a key cannot contain '.' or '..' segments.");
                return null;
            }
            return Canonicalize(s);
        }

        /// <summary>Whether any segment of a forward-slashed key is "." or "..".</summary>
        private static bool HasDotSegment(string key)
        {
            foreach (string part in key.Split('/'))
                if (part == "." || part == "..") return true;
            return false;
        }

        private static readonly Dictionary<string, string> CanonicalCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Rewrites each segment of a key to the casing the folder actually has on disk. A key that
        /// does not correspond to a real folder is returned unchanged, so this can never turn a
        /// usable key into null.
        ///
        /// Cached: the filesystem walk happens once per distinct key, and these are only built when
        /// the selection changes or a Lua state is created.
        /// </summary>
        private static string Canonicalize(string key)
        {
            lock (CanonicalCache)
            {
                string hit;
                if (CanonicalCache.TryGetValue(key, out hit)) return hit;
            }

            string result = key;
            try
            {
                string current = GameRoot;
                string[] parts = key.Split('/');
                var rebuilt = new string[parts.Length];

                for (int i = 0; i < parts.Length; i++)
                {
                    string match = null;
                    foreach (string dir in Directory.GetDirectories(current))
                    {
                        string name = new DirectoryInfo(dir).Name;
                        if (string.Equals(name, parts[i], StringComparison.OrdinalIgnoreCase)) { match = name; break; }
                    }

                    if (match == null) { rebuilt = null; break; }

                    rebuilt[i] = match;
                    current = Path.Combine(current, match);
                }

                if (rebuilt != null) result = string.Join("/", rebuilt);
            }
            catch
            {
                // An unreadable directory just means we keep the key as the game spelled it.
            }

            lock (CanonicalCache) CanonicalCache[key] = result;
            return result;
        }

        /// <summary>
        /// The absolute folder a key names, or null if the key does not land inside the game root.
        ///
        /// Keys are untrusted input on the way back in: they come out of mods' saved JSON and are
        /// built by third-party plugins from whatever a skin or a player handed them.
        /// <see cref="KeyForFolder"/> only ever emits contained keys, so this is that same check in
        /// the other direction -- without it a ".." key, or a rooted one like "C:/Windows" that
        /// Path.Combine returns whole and throws the game root away, resolves outside the install.
        /// </summary>
        public static string FolderForKey(string key)
        {
            if (Str.IsBlank(key)) return null;
            try
            {
                string full = Path.GetFullPath(Path.Combine(GameRoot, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(RootPrefix(), StringComparison.OrdinalIgnoreCase))
                {
                    ModApiPlugin.Log.LogWarning("Refusing the key '" + key + "': it resolves outside the game root.");
                    return null;
                }
                return full;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not resolve the folder for key '" + key + "': " + e.Message);
                return null;
            }
        }

        /// <summary>Whether the folder behind a key ships the given file (e.g. a settings schema).</summary>
        public static bool HasFile(string key, string fileName)
        {
            string folder = FolderForKey(key);
            if (folder == null) return false;
            try { return File.Exists(Path.Combine(folder, fileName)); }
            catch { return false; }
        }

        /// <summary>
        /// Every skin and mode folder that ships <paramref name="fileName"/>, skins first, each
        /// group sorted by name.
        /// </summary>
        public static List<Target> Enumerate(string fileName)
        {
            var found = new List<Target>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectFrom(SkinsDir, SelectorKind.Skin, fileName, found, seen);

            // Modes live directly under mods/, and each mode may carry its own dedicated skins folder.
            List<string> modeFolders = CollectFrom(ModsDir, SelectorKind.Mode, fileName, found, seen);
            foreach (string modeFolder in modeFolders)
                CollectFrom(Path.Combine(modeFolder, "skins"), SelectorKind.Skin, fileName, found, seen);

            found.Sort(delegate (Target a, Target b)
            {
                if (a.Kind != b.Kind) return a.Kind == SelectorKind.Skin ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return found;
        }

        /// <summary>
        /// Scans one container directory a single level deep, descending into all-digit Workshop
        /// containers. Returns every candidate folder inspected (not just the matches), so callers
        /// can walk further down.
        /// </summary>
        private static List<string> CollectFrom(string containerDir, SelectorKind kind, string fileName,
                                                List<Target> into, HashSet<string> seen)
        {
            var inspected = new List<string>();
            if (Str.IsBlank(containerDir) || !SafeDirExists(containerDir)) return inspected;

            foreach (string dir in SafeSubdirectories(containerDir))
            {
                string name = new DirectoryInfo(dir).Name;
                if (WorkshopContainer.IsMatch(name))
                {
                    foreach (string inner in SafeSubdirectories(dir))
                    {
                        inspected.Add(inner);
                        Consider(inner, kind, fileName, into, seen);
                    }
                    continue;
                }

                inspected.Add(dir);
                Consider(dir, kind, fileName, into, seen);
            }
            return inspected;
        }

        private static void Consider(string folder, SelectorKind kind, string fileName,
                                     List<Target> into, HashSet<string> seen)
        {
            try
            {
                if (!File.Exists(Path.Combine(folder, fileName))) return;
                string key = KeyForFolder(folder);
                if (key == null || !seen.Add(key)) return;
                into.Add(new Target
                {
                    Key = key,
                    FolderPath = folder,
                    Name = new DirectoryInfo(folder).Name,
                    Kind = kind
                });
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Skipped '" + folder + "': " + e.Message);
            }
        }

        private static bool SafeDirExists(string path)
        {
            try { return Directory.Exists(path); }
            catch { return false; }
        }

        private static string[] SafeSubdirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not list '" + path + "': " + e.Message);
                return new string[0];
            }
        }
    }
}
