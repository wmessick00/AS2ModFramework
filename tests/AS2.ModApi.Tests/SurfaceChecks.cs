using System;
using System.Collections.Generic;
using System.IO;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>Cold checks for the game event surface: the arity table, and two structural rules</summary>
    // The arity checks are the ones with teeth
    // All four of the game's Messenger classes share one Dictionary<string,Delegate>, so a name has
    // exactly one legal delegate type across the process
    // A wrong argument count does not fail politely in our corner -- the game's own AddListener
    // calls on that name start throwing, and its own broadcasts throw too
    // That is a bug this framework causes in somebody else's code, and reading cannot find it
    // So the expected shapes are written out again here by hand, off the IL read of the shipped
    // Assembly-CSharp
    // Two independent spellings that have to agree is the whole value. Copying them from
    // MessageTable would be worth nothing
    //
    // The source scans cover what tools\verify-invariants.ps1 cannot: that script reads the built
    // DLL, which needs the game installed, and CI has no game
    // Not a substitute -- a scan of source text is weaker than a walk of real IL -- but the fast
    // half of the same idea, and it catches the same mistakes earlier
    internal static class SurfaceChecks
    {
        /// <summary>
        /// Every message the framework carries, and the exact Messenger call shape the game uses to
        /// broadcast it. Read off the IL, not off MessageTable.
        /// </summary>
        private static readonly string[][] Expected =
        {
            new[] { "GameplayStart",          "" },
            new[] { "EndCleanup",             "" },
            new[] { "SongStartedPlaying",     "" },
            new[] { "SongEnded",              "" },
            new[] { "PausingGame",            "" },
            new[] { "ResumingGame",           "" },
            new[] { "PlayerLanded",           "" },
            new[] { "PlayerCrashLanded",      "" },
            new[] { "SkinChanged",            "" },
            new[] { "ModeChanged",            "" },
            new[] { "BrowseSongsClicked",     "" },
            new[] { "ShuttingDown",           "" },
            new[] { "SendingRideScore",       "int" },
            new[] { "TrickStarted",           "int" },
            new[] { "PlayerJumped",           "float" },
            new[] { "ScoreUpdated",           "float" },
            new[] { "SongChanged",            "Song" },
            new[] { "AboutToChangeSong",      "Song" },
            new[] { "LuaSkinError",           "string" },
            new[] { "LuaModError",            "string" },
            new[] { "LuaSkinPrintText",       "string" },
            new[] { "ScoreChanged",           "float,int" },
            new[] { "TrickDone",              "int,int" },
            new[] { "PlayerLandedWithPoints", "int,int" },
            new[] { "TrafficCollected",       "int,int,Vector3" },
            new[] { "TrafficMissed",          "int,int,Vector3" },
        };

        internal static void Run()
        {
            TableMatchesTheVerifiedShapes();
            TableIsWellFormed();
            DeclaresRejectsAWrongArity();
            GameplayEndIsAbsent();
            EveryDeclaredMessageIsWired();
            OnlyTheBridgeTalksToMessenger();
            NothingBroadcasts();
        }

        private static void TableMatchesTheVerifiedShapes()
        {
            if (MessageTable.All.Length != Expected.Length)
            {
                Fail("The message table has " + MessageTable.All.Length + " entries, expected "
                     + Expected.Length + ". A new message needs a row here too.");
                return;
            }

            foreach (string[] row in Expected)
            {
                MessageTable.Entry e = MessageTable.Find(row[0]);
                if (e == null) { Fail("The message table is missing '" + row[0] + "'."); continue; }
                Same("'" + row[0] + "' has the shape the game broadcasts", e.Signature, row[1]);
            }
        }

        private static void TableIsWellFormed()
        {
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool duplicates = false;
            bool blanks = false;
            bool badArity = false;

            foreach (MessageTable.Entry e in MessageTable.All)
            {
                if (string.IsNullOrEmpty(e.Name)) { blanks = true; continue; }
                if (seen.ContainsKey(e.Name)) { duplicates = true; continue; }
                seen[e.Name] = true;

                // Arity is derived from the signature, so a typo such as "int," would inflate it.
                int commas = 0;
                foreach (char c in e.Signature) if (c == ',') commas++;
                int expected = e.Signature.Length == 0 ? 0 : commas + 1;
                if (e.Arity != expected || e.Arity > 3) badArity = true;
            }

            False("no message name is blank", blanks);
            False("no message name appears twice", duplicates);
            False("every arity is derived correctly and is 3 or fewer", badArity);
        }

        private static void DeclaresRejectsAWrongArity()
        {
            True("Declares accepts the right arity", MessageTable.Declares("ScoreChanged", 2));
            False("Declares rejects the wrong arity", MessageTable.Declares("ScoreChanged", 1));
            False("Declares rejects an unknown name", MessageTable.Declares("NotAMessage", 0));
            False("Declares rejects null", MessageTable.Declares(null, 0));
        }

        /// <summary>
        /// "GameplayEnd" reads like the obvious end-of-ride message and is a trap: 32 sites in the
        /// game add or remove a listener for it and no site ever broadcasts it. Anything built on it
        /// would simply never fire. EndCleanup is the real one.
        /// </summary>
        private static void GameplayEndIsAbsent()
        {
            Null("the dead 'GameplayEnd' message is not in the table",
                 MessageTable.Find("GameplayEnd") == null ? null : "GameplayEnd");
        }

        // ---- Source scans ---------------------------------------------------------------------

        private static void EveryDeclaredMessageIsWired()
        {
            string raw = ReadSource("MessengerBridge.cs");
            if (raw == null) { Skip("could not locate src/AS2.ModApi; the source scans did not run"); return; }
            string bridge = CodeOnly(raw);

            var unwired = new List<string>();
            foreach (MessageTable.Entry e in MessageTable.All)
            {
                // The bridge refers to every name through the constant, never as a literal.
                if (bridge.IndexOf("MessageTable." + e.Name, StringComparison.Ordinal) < 0)
                    unwired.Add(e.Name);
            }

            if (unwired.Count == 0) Pass("every declared message is wired in MessengerBridge");
            else Fail("declared but never wired, so the event can never fire: " + string.Join(", ", unwired.ToArray()));
        }

        /// <summary>
        /// The closed set is only closed if one file owns the interop. If a second file starts
        /// calling Messenger directly, the table stops being the description of what we subscribe
        /// to, and the arity guard in MessengerBridge.Claim stops covering everything.
        /// </summary>
        private static void OnlyTheBridgeTalksToMessenger()
        {
            string[] files = ProductionSources();
            if (files == null) { Skip("could not locate src/AS2.ModApi; the source scans did not run"); return; }

            var offenders = new List<string>();
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "MessengerBridge.cs", StringComparison.OrdinalIgnoreCase)) continue;

                string code = CodeOnly(File.ReadAllText(file));
                if (code.IndexOf("Messenger.", StringComparison.Ordinal) >= 0
                    || code.IndexOf("Messenger<", StringComparison.Ordinal) >= 0)
                    offenders.Add(name);
            }

            if (offenders.Count == 0) Pass("only MessengerBridge.cs calls the game's Messenger");
            else Fail("these files call the game's Messenger directly: " + string.Join(", ", offenders.ToArray()));
        }

        /// <summary>
        /// The framework listens and never speaks. verify-invariants.ps1 proves this against the IL
        /// before a release; this catches it on the push that introduces it.
        /// </summary>
        private static void NothingBroadcasts()
        {
            string[] files = ProductionSources();
            if (files == null) { Skip("could not locate src/AS2.ModApi; the source scans did not run"); return; }

            var offenders = new List<string>();
            foreach (string file in files)
            {
                string code = CodeOnly(File.ReadAllText(file));
                if (code.IndexOf("Broadcast", StringComparison.Ordinal) >= 0)
                    offenders.Add(Path.GetFileName(file));
            }

            if (offenders.Count == 0) Pass("no production source broadcasts on the game bus");
            else Fail("these files broadcast: " + string.Join(", ", offenders.ToArray()));
        }

        // ---- Locating the sources ---------------------------------------------------------------

        private static string[] _sources;
        private static bool _searched;

        /// <summary>
        /// Walks up from the test binary to find src/AS2.ModApi. Returns null when it is not there,
        /// which the callers report as a skip rather than a failure: these checks read the repo, and
        /// a run from somewhere the repo is not is a missing check, not a broken framework.
        /// </summary>
        private static string[] ProductionSources()
        {
            if (_searched) return _sources;
            _searched = true;

            try
            {
                DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, Path.Combine("src", "AS2.ModApi"));
                    if (Directory.Exists(candidate))
                    {
                        _sources = Directory.GetFiles(candidate, "*.cs", SearchOption.TopDirectoryOnly);
                        return _sources;
                    }
                    dir = dir.Parent;
                }
            }
            catch { /* treated as "not found" below */ }

            return null;
        }

        /// <summary>The compilable text of a C# file, comments and string literals blanked out</summary>
        // Both scans below search for an identifier, and this repo documents itself heavily
        // The first version searched the raw text and reported MessageTable.cs for explaining what
        // Messenger<T> is. Prose that names the thing it warns about is not a use of it
        // Blanking rather than deleting keeps every offset and line intact, so a future check can
        // still report a position
        private static string CodeOnly(string text)
        {
            char[] outBuf = text.ToCharArray();
            int i = 0;
            int n = text.Length;

            while (i < n)
            {
                char c = text[i];

                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    while (i < n && text[i] != '\n') { outBuf[i] = ' '; i++; }
                    continue;
                }

                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                    {
                        if (text[i] != '\n') outBuf[i] = ' ';
                        i++;
                    }
                    if (i < n) { outBuf[i] = ' '; i++; }        // '*'
                    if (i < n) { outBuf[i] = ' '; i++; }        // '/'
                    continue;
                }

                if (c == '@' && i + 1 < n && text[i + 1] == '"')
                {
                    outBuf[i] = ' '; i++;                        // '@'
                    outBuf[i] = ' '; i++;                        // opening quote
                    while (i < n)
                    {
                        if (text[i] == '"')
                        {
                            // A doubled quote is an escaped quote, not the end of the literal.
                            if (i + 1 < n && text[i + 1] == '"') { outBuf[i] = ' '; outBuf[i + 1] = ' '; i += 2; continue; }
                            outBuf[i] = ' '; i++;
                            break;
                        }
                        if (text[i] != '\n') outBuf[i] = ' ';
                        i++;
                    }
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    outBuf[i] = ' '; i++;
                    while (i < n && text[i] != quote)
                    {
                        if (text[i] == '\\' && i + 1 < n) { outBuf[i] = ' '; i++; }
                        if (i < n) { if (text[i] != '\n') outBuf[i] = ' '; i++; }
                    }
                    if (i < n) { outBuf[i] = ' '; i++; }
                    continue;
                }

                i++;
            }

            return new string(outBuf);
        }

        private static string ReadSource(string fileName)
        {
            string[] files = ProductionSources();
            if (files == null) return null;
            foreach (string f in files)
                if (string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllText(f);
            return null;
        }
    }
}
