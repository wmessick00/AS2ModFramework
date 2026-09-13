using System;

namespace AS2.ModApi
{
    /// <summary>The arithmetic behind the Mod Menu's layout, with nothing from Unity in it</summary>
    // #55. AS2Ui is IMGUI end to end and can never compile in the checks, so the numbers it lays
    // out from sit here instead. Same split, and the same reason, as ScrollTurns.cs -- and before
    // that Str.cs and SelectorKind.cs. The test project's own comment says why: a file added to it
    // must not reach for BepInEx or Unity, and that constraint is what makes the split worth making
    //
    // AS2Ui is the largest file in this assembly and its comments describe layout bugs that were
    // only ever found by eyeballing screenshots -- a slider channel drawn the wrong way round and
    // read off a compressed image, and a resolution scale that "made labels overprint each other"
    // when it was changed. None of that was checked by anything, because none of it could be
    //
    // What could not move is anything returning a Rect: Rect is Unity's, and a public API handing
    // back something else would be a worse trade than the coverage is worth. So the division is
    // that AS2Ui keeps every Rect and every draw, and asks this for every number inside them
    //
    // Everything here takes what it needs as arguments -- no Screen, no Mathf, no statics that a
    // running game fills in. That is what makes it answerable without one
    public static class AS2UiGeometry
    {
        /// <summary>The design resolution every measurement below was taken against</summary>
        public const float DesignWidth = 2560f;
        public const float DesignHeight = 1440f;

        public const float SideMargin = 70f;
        public const float HeaderTop = 44f;
        public const float TitleHeight = 56f;
        public const float SubtitleHeight = 36f;
        public const float HeaderGap = 34f;
        public const float FooterButtonHeight = 60f;
        public const float FooterButtonBaseline = 100f;
        public const float FooterGap = 20f;
        public const float LabelColumnRight = 814f;
        public const float ControlColumnX = 838f;
        public const float ControlColumnWidth = 350f;
        public const float ValueColumnX = 1206f;
        public const float RowPitch = 83f;

        /// <summary>The dialog the game centres on screen, in design units</summary>
        public const float DialogWidth = 1716f;
        public const float DialogHeight = 1216f;

        /// <summary>Pixels per design unit at this screen size</summary>
        // The smaller of the two ratios, because the game scales its dialog on width and not on
        // height -- at 1280x768 its dialog is 858x608 with rows 41.5px apart, and a height-derived
        // scale would give 915x648 and a 44px pitch instead
        // Below 16:9 the smaller ratio reproduces that. Above it, it keeps the dialog on screen
        // rather than taller than the window
        public static float Unit(float screenWidth, float screenHeight)
        {
            return Math.Min(screenWidth / DesignWidth, screenHeight / DesignHeight);
        }

        /// <summary>How tall a header is, with or without its second line</summary>
        public static float HeaderHeight(float unit, bool subtitled)
        {
            return (HeaderTop + TitleHeight + (subtitled ? SubtitleHeight : 0f) + HeaderGap) * unit;
        }

        /// <summary>How tall the footer is: the button row and the gap above it</summary>
        public static float FooterHeight(float unit)
        {
            return (FooterButtonBaseline + FooterGap) * unit;
        }

        /// <summary>How tall a run of settings rows is</summary>
        // Clamped at zero rather than trusted: a caller subtracting two counts can arrive here
        // negative, and a negative height is a rect that draws inside out
        public static float RowsHeight(float unit, int rows)
        {
            return Math.Max(0, rows) * RowPitch * unit;
        }

        /// <summary>Where the content column starts, inside the dialog's side margins</summary>
        public static float ContentX(float dialogX, float unit)
        {
            return dialogX + SideMargin * unit;
        }

        /// <summary>How wide the content column is, with both margins taken off</summary>
        public static float ContentWidth(float dialogWidth, float unit)
        {
            return Math.Max(0f, dialogWidth - 2f * SideMargin * unit);
        }

        /// <summary>How wide the value column is: whatever is left of the row after it starts</summary>
        public static float ValueColumnWidth(float rowWidth, float unit)
        {
            return Math.Max(0f, rowWidth - ValueColumnX * unit);
        }

        /// <summary>
        /// Where a slider's value lands for a mouse at <paramref name="mouseX"/>.
        /// </summary>
        // The handle is positioned by its left edge and grabbed by its middle, which is the whole
        // reason for the half-width: without it the value jumps by half a handle the moment a drag
        // starts, and it is not the kind of wrong that shows up in a screenshot
        // Clamped, because a drag continues to be delivered after the pointer leaves the track
        public static float ValueAt(float mouseX, float trackX, float handleWidth, float usable,
                                    float min, float span)
        {
            return min + Clamp01((mouseX - trackX - handleWidth * 0.5f) / usable) * span;
        }

        /// <summary>Where a scroll view lands for a mouse at <paramref name="mouseY"/></summary>
        // The same shape as ValueAt, on the other axis: grabbed by the middle of the thumb, and
        // clamped because the pointer leaves the track and the drag does not stop
        public static float ScrollAt(float mouseY, float trackY, float thumbSize, float travel,
                                     float hidden)
        {
            return Clamp01((mouseY - trackY - thumbSize * 0.5f) / travel) * hidden;
        }

        /// <summary>Mathf.Clamp01, without Mathf</summary>
        // Division by zero is the case worth naming. A track with no usable length gives infinity
        // or NaN, and only the first of those is clamped by a comparison: NaN fails both, so it has
        // to be tested for. A NaN reaching a Rect is a control that silently stops drawing
        public static float Clamp01(float value)
        {
            if (float.IsNaN(value)) return 0f;
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }
}
