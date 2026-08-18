using System.Reflection;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>
    /// Regression cover for issue #38: one mod that opened a scroll view and never closed it used to
    /// stop every other mod scrolling for the rest of the frame.
    ///
    /// <para>
    /// AS2Ui keeps one scroll view, because IMGUI keeps one clip stack and every mod draws into it.
    /// The bookkeeping around that view therefore decides what one mod's mistake costs everybody
    /// else, which is the same class of bug as issue #16 and issue #26 in ConcurrencyChecks.cs --
    /// shared framework state where a single misbehaving caller degrades the rest.
    /// </para>
    ///
    /// <para>
    /// The rules live in ScrollTurns rather than in AS2Ui so they can be exercised here. AS2Ui is
    /// 1100 lines of IMGUI and cannot be compiled without Unity; the arbitration is arithmetic over
    /// a caller token and a frame number, and needs nothing. That split follows the one AGENTS.md
    /// describes for Str, SelectorKind and ModMenuRegistry.
    /// </para>
    ///
    /// <para>
    /// Every check below was run against the unfixed shape -- one global depth counter, healed only
    /// when the frame number moved on -- before it was trusted. The ones that already passed against
    /// it are here to hold the behaviour the fix had to keep.
    /// </para>
    /// </summary>
    internal static class ScrollChecks
    {
        /// <summary>
        /// A caller token, standing in for the mod assembly AS2Ui passes. ScrollTurns compares these
        /// by reference and reads a name only to write a warning, so any object serves.
        /// </summary>
        private sealed class Mod
        {
            private readonly string _name;
            internal Mod(string name) { _name = name; }
            public override string ToString() { return _name; }
        }

        internal static void Run()
        {
            ALeakedViewDoesNotStopTheNextMod();
            TheLeakWarningNamesBothMods();
            TheNextModOwnsTheViewItWasGiven();
            AModCannotNestItsOwnScrollView();
            AViewLeftOpenAcrossAFrameIsDropped();
            EndFromAModThatDoesNotHoldTheViewIsIgnored();
            EndWithNothingOpenIsIgnored();
            AnOpenThatFailedIsNeverClosed();
            AnAssemblyCallerIsNamedByItsAssemblyName();
        }

        // ---- Regression: issue #38 ---------------------------------------------------------------
        //
        // Each mod draws from its own OnGUI, and Unity calls those in a fixed order within one frame.
        // A mod whose OnGUI returns between BeginScroll and EndScroll -- an early return on some
        // branch, which needs no exception -- used to leave the shared depth counter standing. Every
        // later mod in that same frame then read the standing count as "you are nesting", was refused
        // its GUI.BeginScrollView, and silently stopped scrolling. Healing only when the frame number
        // changed could not help: the leaker re-leaked at the top of every frame, before anybody else
        // ran.

        private static void ALeakedViewDoesNotStopTheNextMod()
        {
            var turns = new ScrollTurns();
            var leaker = new Mod("LeakyMod");
            var victim = new Mod("TidyMod");

            ScrollBegin first = turns.Begin(leaker, 100);
            turns.Opened(true);
            True("issue #38: the first mod is given its scroll view", first.Open);

            // No End from the leaker. The next mod draws in the same frame.
            ScrollBegin second = turns.Begin(victim, 100);

            True("issue #38: a mod that leaked a scroll view does not stop the next mod scrolling",
                 second.Open);
        }

        private static void TheLeakWarningNamesBothMods()
        {
            var turns = new ScrollTurns();
            var leaker = new Mod("LeakyMod");
            var victim = new Mod("TidyMod");

            turns.Begin(leaker, 100);
            turns.Opened(true);
            ScrollBegin second = turns.Begin(victim, 100);

            string warning = second.Warning ?? "";
            True("issue #38: the warning names the mod that leaked the view -- got " + Quote(warning),
                 warning.Contains("LeakyMod"));
            True("issue #38: and the mod that had to drop it -- got " + Quote(warning),
                 warning.Contains("TidyMod"));
        }

        private static void TheNextModOwnsTheViewItWasGiven()
        {
            var turns = new ScrollTurns();
            var leaker = new Mod("LeakyMod");
            var victim = new Mod("TidyMod");

            turns.Begin(leaker, 100);
            turns.Opened(true);
            turns.Begin(victim, 100);
            turns.Opened(true);

            ScrollEnd end = turns.End(victim);

            True("issue #38: and the view it was given is its own to close", end.Close);
            Null("issue #38: which is no misuse, so it warns about nothing", end.Warning);
        }

        // ---- The behaviour the fix had to keep ---------------------------------------------------

        private static void AModCannotNestItsOwnScrollView()
        {
            var turns = new ScrollTurns();
            var mod = new Mod("NestingMod");

            turns.Begin(mod, 100);
            turns.Opened(true);
            ScrollBegin inner = turns.Begin(mod, 100);

            False("a mod that nests its own scroll view is refused the inner one", inner.Open);
            True("and is told which mod did it -- got " + Quote(inner.Warning),
                 (inner.Warning ?? "").Contains("NestingMod"));

            // The inner End closes the inner call, not the view the outer call opened.
            False("closing the inner call does not close the outer view", turns.End(mod).Close);
            True("which the outer End still closes", turns.End(mod).Close);
        }

        private static void AViewLeftOpenAcrossAFrameIsDropped()
        {
            var turns = new ScrollTurns();
            var mod = new Mod("LeakyMod");

            turns.Begin(mod, 100);
            turns.Opened(true);

            // Nobody else draws, so only the next frame can notice.
            ScrollBegin next = turns.Begin(mod, 101);

            True("a view left open across a frame is dropped and the caller drawn again", next.Open);
            True("with a warning that names the mod -- got " + Quote(next.Warning),
                 (next.Warning ?? "").Contains("LeakyMod"));
        }

        private static void EndFromAModThatDoesNotHoldTheViewIsIgnored()
        {
            var turns = new ScrollTurns();
            var owner = new Mod("OwnerMod");
            var stray = new Mod("StrayMod");

            turns.Begin(owner, 100);
            turns.Opened(true);

            ScrollEnd wrong = turns.End(stray);

            False("EndScroll from a mod that does not hold the view closes nothing", wrong.Close);
            True("and says which mod does hold it -- got " + Quote(wrong.Warning),
                 (wrong.Warning ?? "").Contains("OwnerMod"));
            True("while the mod that holds it can still close it", turns.End(owner).Close);
        }

        private static void EndWithNothingOpenIsIgnored()
        {
            var turns = new ScrollTurns();
            var mod = new Mod("EagerMod");

            ScrollEnd end = turns.End(mod);

            False("EndScroll with no view open closes nothing", end.Close);
            True("and names the mod that called it -- got " + Quote(end.Warning),
                 (end.Warning ?? "").Contains("EagerMod"));
        }

        private static void AnOpenThatFailedIsNeverClosed()
        {
            var turns = new ScrollTurns();
            var mod = new Mod("UnluckyMod");

            turns.Begin(mod, 100);
            turns.Opened(false);   // GUI.BeginScrollView threw, so there is nothing to close

            False("a scroll view that never opened is never closed", turns.End(mod).Close);
        }

        private static void AnAssemblyCallerIsNamedByItsAssemblyName()
        {
            var turns = new ScrollTurns();
            Assembly self = typeof(ScrollChecks).Assembly;

            // The real caller is always an assembly, and a name read off one has to survive the trip
            // into a warning. Any misuse will do to produce one.
            ScrollEnd end = turns.End(self);

            True("an assembly caller is named by its assembly name -- got " + Quote(end.Warning),
                 (end.Warning ?? "").Contains(self.GetName().Name));
        }

        /// <summary>A warning as it should read inside a failure message.</summary>
        private static string Quote(string warning)
        {
            return warning == null ? "<null>" : "'" + warning + "'";
        }
    }
}
