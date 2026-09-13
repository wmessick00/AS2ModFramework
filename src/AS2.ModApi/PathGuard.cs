using System;
using System.IO;

namespace AS2.ModApi
{
    /// <summary>The rules, applied wherever this API turns a caller-supplied name into a path</summary>
    // Every name reaching these methods is less trusted than it looks
    // Storage keys are built from Workshop folder names their authors chose. Data file names are
    // built by third-party plugins, sometimes out of those same keys
    // Path.Combine returns an absolute second argument whole and throws the first away, so one
    // unchecked name lands a write meant for BepInEx\data somewhere else entirely
    // IsPlainFileName checks a file name. TargetResolver.FolderForKey is the same idea in the
    // game-root direction
    // IsLink and LinkedSegment answer the half of containment no string handling can: whether a
    // path that reads as inside the install actually leads there
    internal static class PathGuard
    {
        /// <summary>Whether a name is a single file name and nothing more</summary>
        // No directory separator, no drive or stream qualifier, not "." or "..", and not a device
        // The three separators are tested by hand rather than left to GetInvalidFileNameChars,
        // because Mono decides that array's contents by platform and these are exactly the
        // characters containment depends on
        // The array test catches the rest -- control characters, wildcards -- which matter for a
        // well-formed path and not for escaping one
        // The dot names need spelling out separately: they contain no invalid character at all
        // Nor do the device names, and those fail worse (see <see cref="IsDeviceName"/>)
        internal static bool IsPlainFileName(string name)
        {
            if (Str.IsBlank(name)) return false;
            if (name == "." || name == "..") return false;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0) return false;
            if (IsDeviceName(name)) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>The device names that are a fixed word. The numbered ports are IsPortName's</summary>
        private static readonly string[] DeviceNames = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };

        /// <summary>Whether Win32 opens a device rather than a file for this name</summary>
        // #15 -- a mod that writes to a device gets no exception and no file. The write reports
        // success, the bytes go nowhere, and the next launch reads back nothing
        // Every other rejection here stops a write landing in the wrong place. This one stops a
        // write landing nowhere at all
        // The name is often not the mod author's own: AS2Paths.DataFile documents the temptation to
        // name a per-skin file after its storage key, and a key comes from a Workshop folder name
        // Win32 tests the part before the first dot, spaces ignored, so "nul", "NUL", "nul.json",
        // "nul." and "nul .txt" are one device. The whole part must match, so "console.json" and
        // "nullable.json" are ordinary files
        //
        // How far it reaches is not the same on every machine, which is why this refuses by name
        // rather than trying the write and reading the result:
        //     Microsoft's docs, and Windows 10    nul -> device, nul.json -> device
        //     Windows 11 build 26200              nul -> device, nul.json -> ordinary file
        // Checked through cmd.exe and File.WriteAllText together, so it is Windows and not one
        // runtime (see DeviceWritesReallyDisappear in tests/AS2.ModApi.Tests)
        // One player's saved settings survive and another player's do not
        private static bool IsDeviceName(string name)
        {
            int dot = name.IndexOf('.');
            string stem = (dot < 0 ? name : name.Substring(0, dot)).Trim();
            if (stem.Length == 0) return false;

            for (int i = 0; i < DeviceNames.Length; i++)
                if (string.Equals(stem, DeviceNames[i], StringComparison.OrdinalIgnoreCase)) return true;

            return IsPortName(stem);
        }

        /// <summary>
        /// The serial and parallel port devices, COM0-COM9 and LPT0-LPT9.
        ///
        /// Windows also accepts a superscript digit for the first three of each. That is the one part
        /// of this rule a reader would take for a typo, so the three code points are named rather
        /// than typed: every source file here stays ASCII.
        /// </summary>
        private static bool IsPortName(string stem)
        {
            const char SuperscriptOne = (char)0x00B9;
            const char SuperscriptTwo = (char)0x00B2;
            const char SuperscriptThree = (char)0x00B3;

            if (stem.Length != 4) return false;

            bool port = stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                     || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase);
            if (!port) return false;

            char c = stem[3];
            if (c >= '0' && c <= '9') return true;
            return c == SuperscriptOne || c == SuperscriptTwo || c == SuperscriptThree;
        }

        /// <summary>Whether a folder is a junction, a symbolic link or any other reparse point</summary>
        // A name that can lead somewhere other than where it is spelled
        // Containment everywhere else compares strings, and a string comparison cannot see a link
        // "<game>\skins\Foo" is lexically inside the install whether the folder is really there or
        // is a junction to D:\somewhere-else, and GetDirectories and File.Exists follow it either way
        // Creating a junction on Windows needs no elevation, so this is not a privileged trick
        // Win32 GetFileAttributes reports the link's own attributes rather than its target's, which
        // is what makes this answerable -- resolving to the target needs .NET 6 or P/Invoke, and
        // this assembly has neither
        // A path that cannot be read counts as a link. A guard that cannot answer has to refuse, or
        // a failed attribute read quietly switches the check off
        // Only "it is not there" counts as "not a link", because callers legitimately ask about
        // folders that do not exist yet -- a Workshop item still downloading, a key out of saved JSON
        internal static bool IsLink(string path)
        {
            if (Str.IsBlank(path)) return false;
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }
        }

        /// <summary>The first linked component below the root, or null when the path is all real</summary>
        // Every component matters, not just the last. A junction at "skins\Foo" leads as far out as
        // one at "skins\Foo\Bar\Baz"
        // The walk starts below the root, because the root is not ours to judge. BepInEx resolves it
        // from the running process, and a Steam library reached through a junction -- a second
        // drive, an install moved to make room -- is somebody's ordinary setup
        // <paramref name="rootPrefix"/> must already end in a separator, and
        // <paramref name="fullPath"/> must already have passed the lexical containment test
        // This answers the question that test cannot
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

        /// <summary>Opens a file for reading, and only if it really lives inside the root</summary>
        // #56. LinkedSegment answers about a path. This answers about a file, which is a different
        // question and the one a caller actually has
        // The gap it closes: every other guard here proves containment, hands the caller a string,
        // and the caller opens it a moment later. Proving something about a name at one instant
        // does not preserve it across the file operation that follows, and a junction on Windows
        // needs no elevation to create. A Workshop folder mid-download is a folder changing under
        // the check already -- TargetResolver's own comments say so
        //
        // The order is the whole point, and it is the opposite of the obvious one:
        //
        //   1. Open first. On Windows a handle held without delete sharing pins the file: it
        //      cannot be renamed or deleted while this is open, so nothing can be swapped in
        //      underneath it after this line
        //   2. Check second, against the path the handle was opened from. A parent component
        //      swapped to a junction before the open means the open followed it -- and the check
        //      then sees the junction and this refuses, having read nothing
        //   3. Read from the returned stream. Never re-derive the path and open it again; that
        //      puts the window straight back
        //
        // Checking first and opening second is the bug, not the fix: it leaves exactly the window
        // between the two that this exists to remove
        //
        // A component swapped to a junction *after* the open makes this refuse a file that was
        // legitimate. That is the safe direction to be wrong in, and it costs a schema reload
        //
        // FileShare.Read, not ReadWrite: sharing the write is sharing the delete, and the delete is
        // what the pin is for. An author editing the file while the game runs sees one refused read
        // and the next one works
        //
        // Returns null for every refusal and every failure, the same way the guards above do. The
        // caller cannot act differently on "not there" and "not allowed", and a guard that throws
        // inside somebody's game is a guard that takes the game down
        internal static FileStream OpenContained(string fullPath, string rootPrefix)
        {
            if (Str.IsBlank(fullPath) || Str.IsBlank(rootPrefix)) return null;

            FileStream stream = null;
            try
            {
                stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                string linked = LinkedSegment(fullPath, rootPrefix);
                if (linked != null)
                {
                    stream.Dispose();
                    ModApiPlugin.Log.LogWarning(
                        "Refusing to read '" + fullPath + "': '" + linked + "' is a junction or "
                        + "symbolic link, and this API does not follow one -- it cannot tell where "
                        + "the link really leads.");
                    return null;
                }

                return stream;
            }
            catch (Exception e)
            {
                if (stream != null) { try { stream.Dispose(); } catch { /* already failing */ } }

                // Absent is the ordinary case and says nothing worth a line in somebody's log.
                if (!(e is FileNotFoundException) && !(e is DirectoryNotFoundException))
                    ModApiPlugin.Log.LogWarning("Could not open " + fullPath + ": " + e.Message);

                return null;
            }
        }

    }
}
