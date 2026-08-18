using System.Reflection;

namespace AS2.ModApi
{
    /// <summary>What <c>AS2Ui.BeginScroll</c> should do about the one shared scroll view.</summary>
    internal struct ScrollBegin
    {
        /// <summary>Whether the caller should open a scroll view of its own.</summary>
        internal bool Open;

        /// <summary>What to warn about, or null when nothing is wrong.</summary>
        internal string Warning;
    }

    /// <summary>What <c>AS2Ui.EndScroll</c> should do about the one shared scroll view.</summary>
    internal struct ScrollEnd
    {
        /// <summary>Whether the caller should close the scroll view and draw the scrollbar.</summary>
        internal bool Close;

        /// <summary>What to warn about, or null when nothing is wrong.</summary>
        internal string Warning;
    }

    /// <summary>
    /// Decides whose turn the one shared scroll view is, and names the mod when a turn goes wrong.
    ///
    /// <para>
    /// IMGUI keeps one clip stack for the whole process, so AS2Ui can have only one scroll view open
    /// at a time however many mods draw. The bookkeeping around that view therefore decides what one
    /// mod's mistake costs every other mod, and it used to cost them all their scrolling: a mod whose
    /// OnGUI returned between BeginScroll and EndScroll left a global depth counter standing, and
    /// every mod drawn after it in that frame read the standing count as "you are nesting" and was
    /// refused a scroll view of its own. That shipped once, as issue #38.
    /// </para>
    ///
    /// <para>
    /// The fix is an owner. Each turn carries a caller token -- AS2Ui passes the calling assembly,
    /// which is the mod's own DLL -- and the view belongs to whoever opened it. A Begin from a
    /// different mod can only mean the owner never closed its view, so the owner's turn is dropped,
    /// the warning names the owner, and the new caller is given a real scroll view. A mod can now
    /// only break its own scrolling.
    /// </para>
    ///
    /// <para>
    /// Tokens are compared by reference and never held beyond the turn, so a mod that unloads leaves
    /// nothing behind here. A name is read off a token only to write a warning, which keeps the draw
    /// path free of allocation.
    /// </para>
    ///
    /// <para>
    /// Nothing here locks, because OnGUI runs on the main thread only. That is the one assumption in
    /// this file, and it is the same one every GUI call in AS2Ui already makes.
    /// </para>
    ///
    /// <para>
    /// This file holds no Unity and no BepInEx, so tests\AS2.ModApi.Tests compiles it and drives it
    /// cold. AS2Ui cannot go there -- it is IMGUI from end to end -- and these rules are the half a
    /// reader cannot check by looking. See ScrollChecks.cs.
    /// </para>
    /// </summary>
    internal sealed class ScrollTurns
    {
        /// <summary>The caller that opened the view, or null when no view is open.</summary>
        private object _owner;

        /// <summary>
        /// How many Begin calls the owner has made and not yet ended. More than one is the owner
        /// nesting its own lists, which counts here so that the inner End closes the inner call
        /// rather than the view the outer call opened.
        /// </summary>
        private int _depth;

        /// <summary>
        /// Whether the owner's outermost Begin really opened a scroll view. Every entry point in
        /// AS2Ui is allowed to give up and draw nothing, so "did we open one" cannot be assumed
        /// from "was Begin called", and GUI.BeginScrollView and GUI.EndScrollView must balance
        /// exactly or Unity logs for the rest of the session.
        /// </summary>
        private bool _active;

        /// <summary>The frame the owner opened its view in. Only a warning reads this.</summary>
        private int _frame;

        /// <summary>
        /// Claims the scroll view for <paramref name="caller"/> in frame <paramref name="frame"/>.
        /// </summary>
        internal ScrollBegin Begin(object caller, int frame)
        {
            var turn = new ScrollBegin();

            if (_depth > 0)
            {
                // The owner nesting its own lists is the one case that is not a leak. AS2Ui cannot
                // draw a scroll view inside a scroll view, so the inner list is refused -- but the
                // outer view stays open and stays the owner's, which is why the depth counts.
                if (ReferenceEquals(_owner, caller) && _frame == frame)
                {
                    _depth++;
                    turn.Warning = "AS2Ui.BeginScroll does not nest. " + Name(caller)
                                 + " already has a scroll view open, so its inner list will not scroll.";
                    return turn;
                }

                // Anyone else asking means the owner never called EndScroll. Drop its turn and give
                // this caller a real view; refusing one is what issue #38 was.
                //
                // No GUI.EndScrollView goes with this. The owner drew from its own OnGUI, that call
                // has already returned, and Unity resets the clip stack between OnGUI calls -- so
                // there is nothing left to pop, and popping anyway would take a control off some
                // other mod's stack.
                turn.Warning = "AS2Ui.BeginScroll dropped a scroll view that " + Name(_owner)
                             + (_frame == frame ? " opened earlier in this frame"
                                                : " opened in an earlier frame")
                             + " and never closed. That mod has to call AS2Ui.EndScroll on every path "
                             + "out of its OnGUI. " + Name(caller) + " gets a scroll view of its own.";
            }

            _owner = caller;
            _depth = 1;
            _frame = frame;
            _active = false;

            turn.Open = true;
            return turn;
        }

        /// <summary>
        /// Records whether the owner's <c>GUI.BeginScrollView</c> call went through, which decides
        /// whether there is anything for <see cref="End"/> to close.
        /// </summary>
        internal void Opened(bool success)
        {
            _active = success;
        }

        /// <summary>Gives the scroll view back, if <paramref name="caller"/> is the one holding it.</summary>
        internal ScrollEnd End(object caller)
        {
            var turn = new ScrollEnd();

            if (_depth == 0)
            {
                turn.Warning = "AS2Ui.EndScroll was called by " + Name(caller)
                             + " with no scroll view open, so it did nothing. Every EndScroll needs "
                             + "a matching BeginScroll.";
                return turn;
            }

            if (!ReferenceEquals(_owner, caller))
            {
                // Closing somebody else's view would leave that mod drawing its rows into a clip it
                // no longer has, which is the same silent breakage from the other direction.
                turn.Warning = "AS2Ui.EndScroll was called by " + Name(caller) + ", but " + Name(_owner)
                             + " holds the open scroll view. AS2Ui ignored the call.";
                return turn;
            }

            _depth--;
            if (_depth > 0) return turn;   // an inner call of the owner's own; the view stays open

            _owner = null;
            turn.Close = _active;
            _active = false;
            return turn;
        }

        /// <summary>
        /// A caller as it should read in a warning. The real token is the mod's own assembly, so its
        /// simple name is the DLL a reader has to go and look at.
        /// </summary>
        private static string Name(object caller)
        {
            if (caller == null) return "an unidentified mod";

            try
            {
                var assembly = caller as Assembly;
                string name = assembly != null ? assembly.GetName().Name : caller.ToString();
                return string.IsNullOrEmpty(name) ? "an unidentified mod" : name;
            }
            catch
            {
                // A name that throws must not cost the warning it was meant to carry.
                return "an unidentified mod";
            }
        }
    }
}
