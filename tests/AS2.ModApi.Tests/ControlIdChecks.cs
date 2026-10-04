using System;
using System.Reflection;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>Two mods' sliders and scrollbars do not share a control id</summary>
    // #86. AS2Ui.Slider and AS2Ui.Scrollbar asked GUIUtility.GetControlID for an id from one fixed
    // hint, for every mod, and read "is this drag mine" as "does the one process-wide hotControl
    // equal that id". Two mods whose sliders came out as the same-numbered control in their own
    // OnGUI had one id between them, so a drag on one mod's slider moved the other's value
    // The hint is the part of the id the caller chooses. What can be held cold is what it is made
    // from: one owner is one hint however often it is asked, two owners are two, and a slider is
    // never taken for a scrollbar. That AS2Ui passes the right owner is IMGUI and goes unrun here
    internal static class ControlIdChecks
    {
        private const int Slider = 12345;
        private const int Scrollbar = 67890;

        internal static void Run()
        {
            AModGetsTheSameHintEveryTime();
            TwoModsGetTwoHints();
            ASliderIsNotAScrollbar();
            NoOwnerIsTheHintAsItAlwaysWas();
            ANegativeOrLargeHintDoesNotThrow();
        }

        private static void AModGetsTheSameHintEveryTime()
        {
            object mod = typeof(Program).Assembly;

            int first = ControlIds.HintFor(Slider, mod);
            int again = ControlIds.HintFor(Slider, mod);

            True("#86: a mod's slider has the same hint on every event of a drag", first == again);
        }

        private static void TwoModsGetTwoHints()
        {
            Assembly a = typeof(Program).Assembly;
            Assembly b = typeof(string).Assembly;

            True("the two owners the checks use are really two", !ReferenceEquals(a, b));
            True("#86: two mods drawing a slider get two different hints",
                 ControlIds.HintFor(Slider, a) != ControlIds.HintFor(Slider, b));
            True("#86: and two different scrollbars",
                 ControlIds.HintFor(Scrollbar, a) != ControlIds.HintFor(Scrollbar, b));

            // Two owners of one name are still two owners: a plugin loaded twice is two assemblies,
            // and which one is dragging is not something the name can say.
            object one = new object();
            object two = new object();
            True("#86: owners are told apart by identity, not by anything they say about themselves",
                 ControlIds.HintFor(Slider, one) != ControlIds.HintFor(Slider, two));
        }

        private static void ASliderIsNotAScrollbar()
        {
            object mod = typeof(Program).Assembly;

            True("#86: one mod's slider and its scrollbar do not share a hint",
                 ControlIds.HintFor(Slider, mod) != ControlIds.HintFor(Scrollbar, mod));
        }

        private static void NoOwnerIsTheHintAsItAlwaysWas()
        {
            True("#86: with no owner a slider gets the hint it always got", ControlIds.HintFor(Slider, null) == Slider);
            True("#86: and a scrollbar", ControlIds.HintFor(Scrollbar, null) == Scrollbar);
        }

        private static void ANegativeOrLargeHintDoesNotThrow()
        {
            // The hint is a string hash, which is any int, and the mixing must wrap rather than throw
            // in a project built with overflow checking on.
            object mod = typeof(Program).Assembly;

            try
            {
                ControlIds.HintFor(int.MaxValue, mod);
                ControlIds.HintFor(int.MinValue, mod);
                Pass("#86: any hint can be mixed with an owner without overflowing");
            }
            catch (OverflowException)
            {
                Fail("#86: mixing an owner into a hint overflowed");
            }
        }
    }
}
