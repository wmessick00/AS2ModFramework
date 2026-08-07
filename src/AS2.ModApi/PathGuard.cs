using System.IO;

namespace AS2.ModApi
{
    /// <summary>
    /// One rule, applied everywhere this API turns a caller-supplied name into a path.
    ///
    /// Every name that reaches these methods is less trusted than it looks. Storage keys are built
    /// from Steam Workshop folder names, which their authors choose; data file names are built by
    /// third-party plugins, sometimes out of those same keys. And Path.Combine returns an absolute
    /// second argument whole, throwing the first away -- so one unchecked name is all it takes for
    /// a write meant for BepInEx\data to land somewhere else entirely.
    ///
    /// <see cref="TargetResolver.FolderForKey"/> already guards the game-root direction. This is
    /// the same idea for the plain file names the rest of the API accepts.
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
    }
}
