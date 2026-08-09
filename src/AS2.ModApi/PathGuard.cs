using System.IO;

namespace AS2.ModApi
{
    /// <summary>
    /// The rules, applied everywhere this API turns a caller-supplied name into a path.
    ///
    /// Every name that reaches these methods is less trusted than it looks. Storage keys are built
    /// from Steam Workshop folder names, which their authors choose; data file names are built by
    /// third-party plugins, sometimes out of those same keys. And Path.Combine returns an absolute
    /// second argument whole, throwing the first away -- so one unchecked name is all it takes for
    /// a write meant for BepInEx\data to land somewhere else entirely.
    ///
    /// <see cref="IsPlainFileName"/> is that check for the plain file names the rest of the API
    /// accepts; <see cref="TargetResolver.FolderForKey"/> is the same idea in the game-root
    /// direction.
    ///
    /// <see cref="IsLink"/> and <see cref="LinkedSegment"/> answer the half of containment that no
    /// amount of string handling can: whether a path that *reads* as inside the install actually
    /// leads there.
    /// </summary>
    internal static class PathGuard
    {
        /// <summary>
        /// Whether a name is a single file name and nothing more: no directory separator, no drive
        /// or stream qualifier, and not "." or "..".
        ///
        /// The three separators are tested by hand rather than left to GetInvalidFileNameChars
        /// because Mono decides that array's contents by platform, and these are exactly the
        /// characters containment depends on. The array test then catches the rest -- control
        /// characters, wildcards -- which matter for a well-formed path but not for escaping one.
        ///
        /// The dot names need spelling out separately: they contain no invalid character at all.
        /// </summary>
        internal static bool IsPlainFileName(string name)
        {
            if (Str.IsBlank(name)) return false;
            if (name == "." || name == "..") return false;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>
        /// Whether a folder is a junction, a symbolic link, or anything else NTFS calls a reparse
        /// point -- that is, a name that can lead somewhere other than where it is spelled.
        ///
        /// Containment everywhere else here is decided by comparing strings, and a string comparison
        /// cannot see a link: "&lt;game&gt;\skins\Foo" is lexically inside the install whether the
        /// folder is really there or is a junction to D:\somewhere-else, and
        /// Directory.GetDirectories and File.Exists follow it either way. Creating a junction on
        /// Windows needs no elevation, so this is not a privileged trick.
        ///
        /// Win32 GetFileAttributes reports the link's *own* attributes rather than its target's,
        /// which is what makes this answerable at all: resolving a reparse point to its target needs
        /// either .NET 6 or P/Invoke, and this assembly has neither.
        ///
        /// A path that cannot be read is reported as a link. A guard that cannot answer has to
        /// refuse, or a failed attribute read quietly switches the check off. Only "it is not there"
        /// counts as "not a link", because callers legitimately ask about folders that do not exist
        /// yet -- a Workshop item still downloading, a key read back out of saved JSON.
        /// </summary>
        internal static bool IsLink(string path)
        {
            if (Str.IsBlank(path)) return false;
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }
        }

        /// <summary>
        /// The first component of <paramref name="fullPath"/> below <paramref name="rootPrefix"/>
        /// that is a link, or null if the whole path is real folders. Every component matters, not
        /// just the last: a junction at "skins\Foo" leads just as far out as one at
        /// "skins\Foo\Bar\Baz".
        ///
        /// The walk starts *below* the root because the root is not ours to judge. BepInEx resolves
        /// it from the running process, and a Steam library that is itself reached through a
        /// junction -- a second drive, an install moved to make room -- is somebody's ordinary
        /// setup, not an escape from anything.
        ///
        /// <paramref name="rootPrefix"/> must already end in a separator, and
        /// <paramref name="fullPath"/> must already have passed the lexical containment test against
        /// it; this answers the question that test cannot.
        /// </summary>
        internal static string LinkedSegment(string fullPath, string rootPrefix)
        {
            if (Str.IsBlank(fullPath) || Str.IsBlank(rootPrefix)) return null;

            for (int i = rootPrefix.Length; i <= fullPath.Length; i++)
            {
                if (i < fullPath.Length && !IsSeparator(fullPath[i])) continue;

                string sofar = fullPath.Substring(0, i);
                if (sofar.Length <= rootPrefix.Length) continue;              // the root itself
                if (IsSeparator(sofar[sofar.Length - 1])) continue;           // a trailing separator
                if (IsLink(sofar)) return sofar;
            }
            return null;
        }

        private static bool IsSeparator(char c)
        {
            return c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
        }
    }
}
