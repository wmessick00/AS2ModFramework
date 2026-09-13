using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>Cold checks for <see cref="AS2UiGeometry"/></summary>
    // #55. AS2Ui is the largest file in this assembly and was the only one nothing checked, because
    // it is IMGUI end to end and cannot compile here. Its own comments describe layout bugs found
    // by eyeballing screenshots -- a slider channel drawn the wrong way round and read off a
    // compressed image, and a resolution scale that "made labels overprint each other" when it was
    // changed. The numbers behind all of that now sit in their own file, and this is that file
    //
    // Two things are worth checking and neither needs a game: the scale, because it is one
    // expression that every other measurement is multiplied by, and the two drag conversions,
    // because a pointer leaves the track and the drag does not stop
    internal static class UiGeometryChecks
    {
        internal static void Run()
        {
            TheScaleFollowsTheNarrowerSideOfTheScreen();
            TheHeaderGrowsByExactlyItsSecondLine();
            AContentColumnNeverGoesNegative();
            ASliderDragReadsFromTheMiddleOfItsHandle();
            ADragOffTheEndOfTheTrackStaysInRange();
            AScrollDragBehavesTheSameWayOnTheOtherAxis();
            ATrackWithNoLengthDoesNotProduceANaN();
        }

        // ---- The scale ---------------------------------------------------------------------------

        /// <summary>
        /// The game scales its dialog on width, not height, and AS2Ui reproduces that by taking the
        /// smaller of the two ratios. The comment on Unit gives the measured case: at 1280x768 the
        /// game's dialog is 858x608 with rows 41.5px apart, where a height-derived scale would give
        /// 915x648 and a 44px pitch instead.
        /// </summary>
        private static void TheScaleFollowsTheNarrowerSideOfTheScreen()
        {
            True("at the design resolution the scale is exactly one",
                 Near(AS2UiGeometry.Unit(2560f, 1440f), 1f));

            // 16:9 at half size: both ratios agree, and both are a half.
            True("a 16:9 screen scales evenly", Near(AS2UiGeometry.Unit(1280f, 720f), 0.5f));

            // 1280x768 is 5:3, so it is taller than 16:9 and the width is the narrower side.
            True("a screen taller than 16:9 takes its scale from the width",
                 Near(AS2UiGeometry.Unit(1280f, 768f), 1280f / 2560f));

            // 2560x1080 is ultrawide, so the height is the narrower side. Taking the width there
            // would make the dialog taller than the window.
            True("an ultrawide screen takes its scale from the height",
                 Near(AS2UiGeometry.Unit(2560f, 1080f), 1080f / 1440f));

            // The measured case from Unit's own comment, restated as the pitch it produces.
            True("and the row pitch at 1280x768 is the 41.5px the game draws",
                 Near(AS2UiGeometry.RowPitch * AS2UiGeometry.Unit(1280f, 768f), 41.5f));
        }

        // ---- Stacking ---------------------------------------------------------------------------

        /// <summary>
        /// A header with a subtitle is taller than one without by exactly the subtitle, and by
        /// nothing else. This is what the body is measured down from, so a header that is wrong by
        /// a line puts every row below it in the wrong place.
        /// </summary>
        private static void TheHeaderGrowsByExactlyItsSecondLine()
        {
            const float u = 1f;

            float plain = AS2UiGeometry.HeaderHeight(u, false);
            float subtitled = AS2UiGeometry.HeaderHeight(u, true);

            True("a subtitle adds its own height and nothing more",
                 Near(subtitled - plain, AS2UiGeometry.SubtitleHeight * u));

            True("and both scale with the unit",
                 Near(AS2UiGeometry.HeaderHeight(0.5f, true), subtitled * 0.5f));

            True("no rows is no height", Near(AS2UiGeometry.RowsHeight(u, 0), 0f));
            True("three rows is three pitches",
                 Near(AS2UiGeometry.RowsHeight(u, 3), 3f * AS2UiGeometry.RowPitch));
        }

        /// <summary>
        /// A negative width is a rect that draws inside out, and the dialog can be narrower than
        /// its own margins on a small enough window. Clamping is the difference between a column
        /// that vanishes and one that draws over everything to its left.
        /// </summary>
        private static void AContentColumnNeverGoesNegative()
        {
            True("a dialog narrower than its margins gives no content width, not a negative one",
                 Near(AS2UiGeometry.ContentWidth(10f, 1f), 0f));

            True("and a row narrower than its value column does the same",
                 Near(AS2UiGeometry.ValueColumnWidth(10f, 1f), 0f));

            True("a negative row count is no rows rather than a negative height",
                 Near(AS2UiGeometry.RowsHeight(1f, -5), 0f));

            True("an ordinary dialog loses exactly both margins",
                 Near(AS2UiGeometry.ContentWidth(1000f, 1f), 1000f - 2f * AS2UiGeometry.SideMargin));
        }

        // ---- The drag conversions -----------------------------------------------------------------

        /// <summary>
        /// The handle is drawn from its left edge and grabbed by its middle, which is what the
        /// half-width in ValueAt is for. Without it the value jumps by half a handle the instant a
        /// drag begins -- and that is not the kind of wrong a screenshot shows.
        /// </summary>
        private static void ASliderDragReadsFromTheMiddleOfItsHandle()
        {
            // A track from x=100 running 200 usable pixels, a 20px handle, values 0 to 10.
            const float trackX = 100f, handle = 20f, usable = 200f, min = 0f, span = 10f;

            True("the middle of the handle at the start of the track is the minimum",
                 Near(AS2UiGeometry.ValueAt(trackX + handle * 0.5f, trackX, handle, usable, min, span), 0f));

            True("halfway along is the middle of the range",
                 Near(AS2UiGeometry.ValueAt(trackX + handle * 0.5f + usable * 0.5f,
                                            trackX, handle, usable, min, span), 5f));

            True("the far end is the maximum",
                 Near(AS2UiGeometry.ValueAt(trackX + handle * 0.5f + usable,
                                            trackX, handle, usable, min, span), 10f));

            // The half-handle is the point, and this is where it shows. At either end both
            // readings clamp to the same bound, so the difference is only visible in the
            // middle: the value trails the pointer by half a handle's worth of the span.
            float trailing = (handle * 0.5f / usable) * span;
            True("the grab point is the middle of the handle, not its left edge",
                 Near(AS2UiGeometry.ValueAt(trackX + usable * 0.5f, trackX, handle, usable, min, span),
                      5f - trailing));

            True("a range that does not start at zero is offset, not rescaled",
                 Near(AS2UiGeometry.ValueAt(trackX + handle * 0.5f, trackX, handle, usable, 3f, 4f), 3f));
        }

        /// <summary>
        /// A drag keeps being delivered after the pointer leaves the track, on both sides. Both
        /// ends have to hold rather than run away with the value.
        /// </summary>
        private static void ADragOffTheEndOfTheTrackStaysInRange()
        {
            const float trackX = 100f, handle = 20f, usable = 200f;

            True("dragging far past the left end holds at the minimum",
                 Near(AS2UiGeometry.ValueAt(-9999f, trackX, handle, usable, 0f, 10f), 0f));

            True("and far past the right end holds at the maximum",
                 Near(AS2UiGeometry.ValueAt(9999f, trackX, handle, usable, 0f, 10f), 10f));
        }

        private static void AScrollDragBehavesTheSameWayOnTheOtherAxis()
        {
            const float trackY = 50f, thumb = 30f, travel = 100f, hidden = 400f;

            True("the middle of the thumb at the top of the track is the top of the content",
                 Near(AS2UiGeometry.ScrollAt(trackY + thumb * 0.5f, trackY, thumb, travel, hidden), 0f));

            True("the bottom of the track is everything that was hidden",
                 Near(AS2UiGeometry.ScrollAt(trackY + thumb * 0.5f + travel, trackY, thumb, travel, hidden),
                      400f));

            True("and dragging past the bottom holds there",
                 Near(AS2UiGeometry.ScrollAt(9999f, trackY, thumb, travel, hidden), 400f));

            True("dragging above the top holds at zero",
                 Near(AS2UiGeometry.ScrollAt(-9999f, trackY, thumb, travel, hidden), 0f));
        }

        /// <summary>
        /// The case a clamp written with two comparisons gets wrong. A track with no usable length
        /// divides by zero: that is infinity for a live pointer, which a comparison does catch, and
        /// NaN for a pointer exactly on the grab point, which fails both comparisons and passes
        /// straight through. A NaN reaching a Rect is a control that silently stops drawing.
        /// </summary>
        private static void ATrackWithNoLengthDoesNotProduceANaN()
        {
            float value = AS2UiGeometry.ValueAt(100f, 100f, 0f, 0f, 0f, 10f);
            True("a slider with no usable track gives a real number (" + value + ")", IsReal(value));

            float scroll = AS2UiGeometry.ScrollAt(50f, 50f, 0f, 0f, 400f);
            True("a scrollbar with no travel gives a real number (" + scroll + ")", IsReal(scroll));

            True("and the clamp answers zero rather than NaN", Near(AS2UiGeometry.Clamp01(float.NaN), 0f));
            True("infinity clamps to one", Near(AS2UiGeometry.Clamp01(float.PositiveInfinity), 1f));
            True("negative infinity clamps to zero", Near(AS2UiGeometry.Clamp01(float.NegativeInfinity), 0f));
        }

        // ---- Plumbing ------------------------------------------------------------------------------

        /// <summary>Two floats that are the same number as far as a layout is concerned</summary>
        private static bool Near(float actual, float expected)
        {
            if (float.IsNaN(actual) || float.IsNaN(expected)) return false;
            float difference = actual - expected;
            if (difference < 0f) difference = -difference;
            return difference < 0.0001f;
        }

        private static bool IsReal(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
