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
        ///
        /// "Under the game root" is two questions, because a path can read as contained without
        /// being contained: see <see cref="PathGuard.LinkedSegment"/>.
        /// </summary>
        public static string KeyForFolder(string absoluteFolder)
        {
            if (Str.IsBlank(absoluteFolder)) return null;
            try
            {
                string full = Path.GetFullPath(absoluteFolder);
                string root = RootPrefix();
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

                string linked = PathGuard.LinkedSegment(full, root);
                if (linked != null)
                {
                    ModApiPlugin.Log.LogWarning("Could not build a key for '" + absoluteFolder + "': '" + linked +
                                                "' is a junction or symbolic link, and this API does not follow one -- it cannot tell where the link really leads.");
                    return null;
                }

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
        ///
        /// An absolute path is rejected for the same reason. <see cref="FolderForKey"/> would catch
        /// one on the way back in, but this is the method documented as producing a key, and a key
        /// does not stay here: it is handed to mods, written into their saved JSON, and combined
        /// with paths by code this API never sees. The two guards are deliberately symmetric.
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

            // A drive or stream qualifier ("C:/Windows", "skins/foo:bar") means this is not a
            // location inside the install. Path.IsPathRooted would answer the first case, but it
            // throws on invalid path characters and these strings arrive from the game and from
            // third-party mods; ':' is not legal inside a Windows path segment anyway, so testing
            // for it directly is both safer to call and stricter than the question asked.
            if (s.IndexOf(':') >= 0)
            {
                ModApiPlugin.Log.LogWarning("Refusing the relative path '" + relativePath + "': a key names a folder inside the game, so it cannot be absolute.");
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
        /// A backstop rather than the real bound. Only keys that resolved to a folder on disk are
        /// cached, so the dictionary is already limited by what the install contains; this caps it
        /// anyway rather than trusting that reasoning to survive a future edit. Clearing wholesale
        /// rather than evicting is enough -- a miss costs one directory listing.
        /// </summary>
        private const int MaxCachedKeys = 4096;

        /// <summary>
        /// Rewrites each segment of a key to the casing the folder actually has on disk. A key that
        /// does not correspond to a real folder is returned unchanged, so this can never turn a
        /// usable key into null.
        ///
        /// **Only successful resolutions are cached.** A key that did not resolve is walked again
        /// next time, which costs one directory listing and buys the case that actually happens: a
        /// Steam Workshop item still downloading when something first asks about it. Caching that
        /// failure would pin the caller's own spelling for the rest of the process -- exactly the
        /// fork in a mod's saved JSON this method exists to prevent, and it would defeat it in the
        /// one session where the folder appeared late.
        ///
        /// Successes need no equivalent treatment. A folder already on disk does not change how it
        /// spells itself mid-session, and a rename produces a different key, which is a different
        /// cache entry and so a fresh walk. A case-only rename is the sole stale case left, and on
        /// Windows both spellings name the same folder, so it costs nothing but the spelling.
        /// </summary>
        private static string Canonicalize(string key)
        {
            lock (CanonicalCache)
            {
                string hit;
                if (CanonicalCache.TryGetValue(key, out hit)) return hit;
            }

            string resolved = ResolveCasing(key);
            if (resolved == null) return key;

            lock (CanonicalCache)
            {
                if (CanonicalCache.Count >= MaxCachedKeys) CanonicalCache.Clear();
                CanonicalCache[key] = resolved;
            }
            return resolved;
        }

        /// <summary>
        /// The key with every segment respelled the way the folder on disk spells it, or null if
        /// the walk did not find a real folder for each one. Null is "ask me again later", which is
        /// what keeps a folder that appears mid-session from being missed for the whole session.
        /// </summary>
        private static string ResolveCasing(string key)
        {
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

                    if (match == null) return null;

                    // A linked folder is not part of the install, so there is no on-disk casing here
                    // worth adopting. Returning null leaves the key spelled the way the caller spelled
                    // it and, just as importantly, does not cache it; FolderForKey refuses it either
                    // way, and this way nothing here has quietly blessed the path as real.
                    string child = Path.Combine(current, match);
                    if (PathGuard.IsLink(child)) return null;

                    rebuilt[i] = match;
                    current = child;
                }

                return string.Join("/", rebuilt);
            }
            catch
            {
                // An unreadable directory just means we keep the key as the game spelled it -- and
                // that we do not remember having failed, since the next call may well succeed.
                return null;
            }
        }

        /// <summary>
        /// The absolute folder a key names, or null if the key does not land inside the game root.
        ///
        /// Keys are untrusted input on the way back in: they come out of mods' saved JSON and are
        /// built by third-party plugins from whatever a skin or a player handed them.
        /// <see cref="KeyForFolder"/> only ever emits contained keys, so this is that same check in
        /// the other direction -- without it a ".." key, or a rooted one like "C:/Windows" that
        /// Path.Combine returns whole and throws the game root away, resolves outside the install.
        ///
        /// The string test is necessary and not sufficient. A key can name a folder that is spelled
        /// inside the install and is a junction to somewhere else entirely, which every path
        /// operation below this point would follow without complaint; see
        /// <see cref="PathGuard.LinkedSegment"/>.
        /// </summary>
        public static string FolderForKey(string key)
        {
            if (Str.IsBlank(key)) return null;
            try
            {
                string root = RootPrefix();
                string full = Path.GetFullPath(Path.Combine(GameRoot, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    ModApiPlugin.Log.LogWarning("Refusing the key '" + key + "': it resolves outside the game root.");
                    return null;
                }

                string linked = PathGuard.LinkedSegment(full, root);
                if (linked != null)
                {
                    ModApiPlugin.Log.LogWarning("Refusing the key '" + key + "': '" + linked +
                                                "' is a junction or symbolic link, and this API does not follow one -- it cannot tell where the link really leads.");
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

        /// <summary>
        /// Whether the folder behind a key ships the given file (e.g. a settings schema).
        /// <paramref name="fileName"/> must be a plain file name; see <see cref="PathGuard"/>.
        /// </summary>
        public static bool HasFile(string key, string fileName)
        {
            if (!PathGuard.IsPlainFileName(fileName))
            {
                ModApiPlugin.Log.LogWarning("Refusing the file name '" + fileName + "': it must be a plain file name, "
                                          + "not a path and not a Windows device name.");
                return false;
            }

            string folder = FolderForKey(key);
            if (folder == null) return false;
            try { return File.Exists(Path.Combine(folder, fileName)); }
            catch { return false; }
        }

        /// <summary>
        /// Every skin and mode folder that ships <paramref name="fileName"/>, skins first, each
        /// group sorted by name.
        ///
        /// <paramref name="fileName"/> must be a plain file name. An absolute one would make the
        /// existence test below true for every folder inspected, so this would answer "all of
        /// them" -- a wrong answer that looks exactly like a working one.
        /// </summary>
        public static List<Target> Enumerate(string fileName)
        {
            var found = new List<Target>();

            if (!PathGuard.IsPlainFileName(fileName))
            {
                ModApiPlugin.Log.LogWarning("Refusing to enumerate targets for '" + fileName + "': it must be a plain "
                                          + "file name, not a path and not a Windows device name.");
                return found;
            }

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
        ///
        /// The container itself is checked for being a link because it is the one path here that
        /// does not arrive through <see cref="SafeSubdirectories"/>: skins\, mods\ and a mode's own
        /// skins\ are all composed rather than listed. KeyForFolder would refuse whatever came back
        /// anyway, but only after this had already walked somebody else's disk.
        ///
        /// <para>
        /// An all-digit name is not by itself enough to say a folder is a Workshop container rather
        /// than a plain local skin or mode somebody happened to name with digits ("skins/2024",
        /// "mods/7"). A real container never carries the schema/content file directly -- only the
        /// items inside it do -- so a digit-named folder that does ship
        /// <paramref name="fileName"/> itself is treated as a target and never descended into (see
        /// issue #33). Without this, such a folder was silently skipped: <c>Consider</c> was never
        /// called on the container itself, and its contents are not shaped like a container's, so
        /// nothing inside it matched either.
        /// </para>
        /// </summary>
        private static List<string> CollectFrom(string containerDir, SelectorKind kind, string fileName,
                                                List<Target> into, HashSet<string> seen)
        {
            var inspected = new List<string>();
            if (Str.IsBlank(containerDir) || !SafeDirExists(containerDir)) return inspected;

            if (PathGuard.IsLink(containerDir))
            {
                ModApiPlugin.Log.LogWarning("Not looking inside '" + containerDir +
                                            "': it is a junction or symbolic link, and this API only manages folders that really live in the game folder.");
                return inspected;
            }

            foreach (string dir in SafeSubdirectories(containerDir))
            {
                string name = new DirectoryInfo(dir).Name;
                if (WorkshopContainer.IsMatch(name) && !HasFileDirectly(dir, fileName))
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

        /// <summary>
        /// Whether a candidate folder ships <paramref name="fileName"/> directly inside it, which is
        /// what tells a digit-named local target apart from a Workshop container of the same shape
        /// before deciding to look inside it instead of at it. Errors read as "no" rather than
        /// throwing: the caller falls back to treating the folder as a container either way, which is
        /// the behaviour this repo already had for everything that is not a digit-named target.
        /// </summary>
        private static bool HasFileDirectly(string folder, string fileName)
        {
            try { return File.Exists(Path.Combine(folder, fileName)); }
            catch { return false; }
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

        /// <summary>
        /// The subdirectories of a folder that are really there. Links are dropped rather than
        /// followed, which is what keeps an enumeration from wandering off the install: a junction
        /// under skins\ is listed by Directory.GetDirectories exactly like a folder, and everything
        /// downstream -- the recursion into a mode's skins\, the File.Exists, the key -- would treat
        /// its contents as install content.
        /// </summary>
        private static string[] SafeSubdirectories(string path)
        {
            string[] all;
            try { all = Directory.GetDirectories(path); }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not list '" + path + "': " + e.Message);
                return new string[0];
            }

            var real = new List<string>(all.Length);
            foreach (string dir in all)
            {
                if (PathGuard.IsLink(dir))
                {
                    ModApiPlugin.Log.LogWarning("Skipped '" + dir +
                                                "': it is a junction or symbolic link, and this API only manages folders that really live in the game folder.");
                    continue;
                }
                real.Add(dir);
            }
            return real.ToArray();
        }
    }
}
