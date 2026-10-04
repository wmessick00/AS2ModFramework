using System;
using System.IO;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>OpenContained asks the handle where the file really is, not only the path</summary>
    // #84. The open comes first so nothing can be swapped in under the handle, and the check for a
    // link comes second, against the path as it is by then. A parent that was a junction at the open
    // and an ordinary folder by the check makes the open follow the link and the check find nothing,
    // and the caller is handed a stream on a file outside the install, proved contained
    // A junction that has already gone cannot be staged on a runner, and it is the case that matters.
    // So the two lookups that say where a handle and a folder really are can be replaced, the way
    // FilePresence's probe is in the sibling repository, and these drive the same decision with the
    // answers a followed-and-removed junction would produce
    // What cannot be shown here is the Windows call itself. Run on Windows it is the real one, and
    // every OpenContained check in Program.cs goes through it
    internal static class HandleChecks
    {
        private static string _root;
        private static string _outside;
        private static string _file;

        internal static void Run()
        {
            string stem = Path.Combine(Path.GetTempPath(), "as2-handle-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _root = stem;
            _outside = stem + "-outside";
            _file = Path.Combine(_root, "skins", "Plain", "data.json");

            Func<FileStream, string> handle = PathGuard.HandlePathOf;
            Func<string, string> folder = PathGuard.FolderPathOf;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file, "-- inside");
                Directory.CreateDirectory(_outside);
                File.WriteAllText(Path.Combine(_outside, "data.json"), "-- outside");

                AFileThatReallyLivesOutsideIsRefused();
                AFileThatReallyLivesInsideIsOpened();
                AnAnswerThatCannotBeGivenLeavesTheOtherCheckAlone();
                TheComparisonIsAboutTheRootNotTheSpelling();
                WhatWindowsAnswersIsReadOnlyInTheFormsItGives();
            }
            finally
            {
                PathGuard.HandlePathOf = handle;
                PathGuard.FolderPathOf = folder;

                try { Directory.Delete(_root, true); } catch { }
                try { Directory.Delete(_outside, true); } catch { }
            }
        }

        private static string RootPrefix()
        {
            return _root + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// The case the issue describes. The path reads as inside, and nothing on it is a link by the
        /// time anything looks, but the handle that was opened is on a file outside, because a parent
        /// was a junction at the instant of the open.
        /// </summary>
        private static void AFileThatReallyLivesOutsideIsRefused()
        {
            PathGuard.FolderPathOf = delegate (string f) { return _root; };
            PathGuard.HandlePathOf = delegate (FileStream s) { return Path.Combine(_outside, "data.json"); };

            ModApiPlugin.Log.Clear();
            FileStream stream = PathGuard.OpenContained(_file, RootPrefix());

            Null("#84: a handle that is really outside the install is refused, whatever the path now says",
                 stream == null ? null : "a stream was handed back");
            True("#84: and the refusal says where the file really was",
                 ModApiPlugin.Log.Mentions(Path.Combine(_outside, "data.json")));
            True("#84: and that a link was the reason", ModApiPlugin.Log.Mentions("a link was followed"));

            if (stream != null) stream.Dispose();

            // The refusal gave the handle back. A pinned file cannot be renamed, so this is the
            // evidence that the refusal did not leave it open.
            string moved = _file + ".moved";
            bool renamed = true;
            try { File.Move(_file, moved); } catch { renamed = false; }
            True("#84: and the refused handle was closed, not left holding the file", renamed);
            if (renamed) File.Move(moved, _file);

            // A sibling whose name merely begins with the root's is not inside it.
            PathGuard.HandlePathOf = delegate (FileStream s) { return Path.Combine(_root + "-outside", "data.json"); };
            Null("#84: a folder that only starts with the root's name is not the root",
                 PathGuard.OpenContained(_file, RootPrefix()) == null ? null : "a stream was handed back");
        }

        private static void AFileThatReallyLivesInsideIsOpened()
        {
            PathGuard.FolderPathOf = delegate (string f) { return _root; };
            PathGuard.HandlePathOf = delegate (FileStream s) { return _file; };

            using (FileStream stream = PathGuard.OpenContained(_file, RootPrefix()))
            {
                True("#84: a handle that is really inside the install is handed back", stream != null);

                if (stream != null)
                    using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 16, true))
                        Same("#84: and it reads the file it proved", reader.ReadToEnd(), "-- inside");
            }
        }

        /// <summary>
        /// Off Windows there is no such call, and on it the call can fail. Neither is a reason to
        /// refuse every read the API makes: the answer is "cannot tell", and the check above, which
        /// has always stood alone, is what decides.
        /// </summary>
        private static void AnAnswerThatCannotBeGivenLeavesTheOtherCheckAlone()
        {
            PathGuard.FolderPathOf = delegate (string f) { return _root; };
            PathGuard.HandlePathOf = delegate (FileStream s) { return null; };
            using (FileStream a = PathGuard.OpenContained(_file, RootPrefix()))
                True("#84: a handle whose place cannot be told is not refused for it", a != null);

            PathGuard.HandlePathOf = delegate (FileStream s) { return Path.Combine(_outside, "data.json"); };
            PathGuard.FolderPathOf = delegate (string f) { return null; };
            using (FileStream b = PathGuard.OpenContained(_file, RootPrefix()))
                True("#84: nor is one whose root's place cannot be told", b != null);

            PathGuard.FolderPathOf = delegate (string f) { return _root; };
            PathGuard.HandlePathOf = delegate (FileStream s) { throw new InvalidOperationException("the call failed"); };
            using (FileStream c = PathGuard.OpenContained(_file, RootPrefix()))
                True("#84: nor is a lookup that throws", c != null);
        }

        /// <summary>
        /// What is compared is the handle and the root, spelled the way the OS spells both. The
        /// string the caller gave is not part of it: a root reached through a junction, a mapped
        /// drive and a name written with short names all spell differently from what the OS reports,
        /// and none of them is a reason to refuse a read.
        /// </summary>
        private static void TheComparisonIsAboutTheRootNotTheSpelling()
        {
            // The root the OS reports is somewhere else entirely, and the file is under that.
            string realRoot = Path.Combine(_outside, "real-root");

            PathGuard.FolderPathOf = delegate (string f) { return realRoot; };
            PathGuard.HandlePathOf = delegate (FileStream s) { return Path.Combine(realRoot, "skins", "Plain", "data.json"); };
            using (FileStream a = PathGuard.OpenContained(_file, RootPrefix()))
                True("#84: a root that is really somewhere else is judged where it really is", a != null);

            // Different case, as Windows reports a name and a caller spells it.
            PathGuard.FolderPathOf = delegate (string f) { return _root.ToUpperInvariant(); };
            PathGuard.HandlePathOf = delegate (FileStream s) { return _file.ToLowerInvariant(); };
            using (FileStream b = PathGuard.OpenContained(_file, RootPrefix()))
                True("#84: case is not part of the comparison", b != null);

            True("#84: a path under a folder is inside it", PathGuard.IsInside(_file, _root));
            True("#84: whatever separator it is written with",
                 PathGuard.IsInside(_file.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _root));
            True("#84: and whether the folder is given with a trailing separator",
                 PathGuard.IsInside(_file, _root + Path.DirectorySeparatorChar));
            False("#84: a sibling that shares its start is not inside it",
                  PathGuard.IsInside(Path.Combine(_root + "x", "a.json"), _root));
            False("#84: the folder is not inside itself", PathGuard.IsInside(_root, _root));
            False("#84: and nor is a path that merely shares its parent",
                  PathGuard.IsInside(Path.Combine(_outside, "a.json"), _root));
        }

        private static void WhatWindowsAnswersIsReadOnlyInTheFormsItGives()
        {
            Same("#84: a drive path loses the prefix Windows puts on it",
                 PathGuard.WithoutPrefix(@"\\?\C:\Games\AS2\skins\a.json"), @"C:\Games\AS2\skins\a.json");
            Same("#84: a share becomes an ordinary network path",
                 PathGuard.WithoutPrefix(@"\\?\UNC\server\share\a.json"), @"\\server\share\a.json");
            Same("#84: and the prefix is read without regard to case",
                 PathGuard.WithoutPrefix(@"\\?\unc\server\share\a.json"), @"\\server\share\a.json");

            Null("#84: a volume name is not a form this knows", PathGuard.WithoutPrefix(@"\\?\Volume{1234}\a.json"));
            Null("#84: nor a path with no prefix at all", PathGuard.WithoutPrefix(@"C:\Games\a.json"));
            Null("#84: nor a drive with nothing after it", PathGuard.WithoutPrefix(@"\\?\C:"));
            Null("#84: nor nothing", PathGuard.WithoutPrefix(null));
        }
    }
}
