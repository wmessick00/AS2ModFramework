using System;
using System.Collections.Generic;

namespace AS2.Tests
{
    /// <summary>
    /// The assertions and the summary every test project here shares.
    ///
    /// This file is linked into each test project rather than built into a library of its own,
    /// matching how those projects link the production sources they cover. Two projects now report
    /// results, and the reporting is a convention rather than an obvious one -- PASS lines as they
    /// happen, SKIP repeated at the end so an unexercised guard cannot pass for coverage, exit code
    /// 0 or 1 for CI. Stated once, it stays the same in both.
    ///
    /// Call the assertions unqualified through `using static AS2.Tests.Check;`, and end Main with
    /// `return Report();`.
    ///
    /// There is no test framework under this on purpose: the repo takes no NuGet dependency, and
    /// `dotnet run` plus an exit code is understood by every CI there is. See the comment in
    /// tests/AS2.ModApi.Tests/AS2.ModApi.Tests.csproj.
    /// </summary>
    internal static class Check
    {
        private static int _passed;
        private static readonly List<string> Failures = new List<string>();
        private static readonly List<string> Skipped = new List<string>();

        /// <summary>Two strings that must match exactly, including their casing.</summary>
        internal static void Same(string what, string actual, string expected)
        {
            // Ordinal: several of these are asserting which casing came back.
            if (string.Equals(actual, expected, StringComparison.Ordinal)) Pass(what);
            else Fail(what + " -- expected '" + Show(expected) + "', got '" + Show(actual) + "'");
        }

        internal static void Null(string what, string actual)
        {
            if (actual == null) Pass(what);
            else Fail(what + " -- expected null, got '" + actual + "'");
        }

        internal static void True(string what, bool actual)
        {
            if (actual) Pass(what); else Fail(what + " -- expected true");
        }

        internal static void False(string what, bool actual)
        {
            if (!actual) Pass(what); else Fail(what + " -- expected false");
        }

        internal static void Pass(string what)
        {
            _passed++;
            Console.WriteLine("  PASS  " + what);
        }

        internal static void Fail(string what)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what);
        }

        /// <summary>
        /// A check the machine could not set up. Not a failure -- there is nothing wrong with the
        /// code -- but it is printed again in the summary, because a guard nobody exercised looks
        /// exactly like a guard that works.
        /// </summary>
        internal static void Skip(string why)
        {
            Skipped.Add(why);
            Console.WriteLine("  SKIP  " + why);
        }

        /// <summary>Prints the summary and returns the exit code Main should return.</summary>
        internal static int Report()
        {
            Console.WriteLine();
            foreach (string s in Skipped) Console.WriteLine("SKIPPED: " + s);

            if (Failures.Count == 0)
            {
                Console.WriteLine("All " + _passed + " checks passed.");
                return 0;
            }

            Console.WriteLine(Failures.Count + " FAILED (" + _passed + " passed):");
            foreach (string f in Failures) Console.WriteLine("  - " + f);
            return 1;
        }

        /// <summary>
        /// A value as it should read in a failure message. Line breaks and tabs are printed as
        /// escapes: a file-content check that failed on a trailing newline is unreadable otherwise,
        /// because the difference does not survive being printed.
        /// </summary>
        private static string Show(string s)
        {
            if (s == null) return "<null>";
            return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
