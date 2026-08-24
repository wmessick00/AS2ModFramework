using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace AS2.ModApi
{
    // Geometry
    // ===========================================================================================
    // Every value is in the game's 1440p design space and scaled by Unit, so a number measured off
    // a 2560x1440 screenshot can be typed in directly
    // Positions are offsets from the centre of the screen or of DialogRect, not from a screen edge,
    // because the dialog is centred and everything in it moves with it
    // The measurements and where they came from: see the AS2Ui Controls wiki page

    /// <summary>Draws IMGUI that looks like the game's own settings dialog</summary>
    // The game's settings menu is EZGUI, not IMGUI, and its rows come from prefabs via
    // Menu.setContents, so it cannot be appended to
    // Reproducing the look is less fragile and the only practical option for per-skin content
    public static class AS2Ui
    {
        /// <summary>The design resolution everything below was measured against</summary>
        public const float DesignWidth = 2560f;
        public const float DesignHeight = 1440f;

        // ---- Palette, sampled from the game's settings dialog --------------------------------

        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.60f);
        public static readonly Color PanelBg = new Color(0.043f, 0.043f, 0.043f, 1f);
        public static readonly Color PanelBorder = new Color(0.17f, 0.17f, 0.17f, 1f);
        public static readonly Color TextColor = Color.white;
        public static readonly Color DimText = new Color(0.62f, 0.62f, 0.62f, 1f);

        /// <summary>The track behind the handle. The game draws this white</summary>
        public static readonly Color SliderFilled = Color.white;

        /// <summary>
        /// The track ahead of the handle. The game draws this blue, which is why a slider at its
        /// maximum (Music Volume 100%, Graphics Level Ultra) shows no blue at all.
        /// </summary>
        public static readonly Color SliderRemainder = new Color(0.11f, 0.44f, 0.89f, 1f);

        public static readonly Color ButtonBg = new Color(0.34f, 0.34f, 0.34f, 1f);
        public static readonly Color ButtonBgHover = new Color(0.45f, 0.45f, 0.45f, 1f);
        public static readonly Color ButtonBgDisabled = new Color(0.20f, 0.20f, 0.20f, 1f);

        // ---- Geometry -------------------------------------------------------------------------

        /// <summary>Pixels per design unit. Multiply any measured 1440p value by this</summary>
        // The game scales its dialog on width, not height
        // At 1280x768 its dialog is 858x608 (1716x1216 at half scale) with rows 41.5px apart
        // A height-derived scale would give 915x648 and a 44px pitch instead
        // The smaller of the two ratios reproduces that below 16:9, and above 16:9 keeps the
        // dialog on screen rather than taller than the window
        public static float Unit
        {
            get { return Mathf.Min(Screen.width / DesignWidth, Screen.height / DesignHeight); }
        }

        /// <summary>Where the game centres its dialog: 1716 x 1216 design units, screen centred</summary>
        public static Rect DialogRect
        {
            get
            {
                float u = Unit;
                float w = 1716f * u, h = 1216f * u;
                return new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            }
        }

        /// <summary>Where a mod's entry button belongs, left of the dialog's Back/Next/OK row</summary>
        // Same baseline as Back/Next/OK, 60 units above the dialog's bottom edge, centred 666 units
        // left of the dialog's centre
        // Anchored to DialogRect, not to the screen. A y measured down from the top of the screen
        // only holds at the design resolution -- at 1280x768 it put the button below the dialog's
        // bottom edge and outside its left one
        public static Rect EntryButtonRect
        {
            get
            {
                float u = Unit;
                Rect d = DialogRect;
                float w = 320f * u, h = 60f * u;
                return new Rect(d.center.x - (666f * u) - w * 0.5f, d.yMax - (60f * u) - h * 0.5f, w, h);
            }
        }

        // ---- Dialog chrome -----------------------------------------------------------------------
        //
        // Where a panel's title, body and buttons go inside DialogRect. These are the numbers the two
        // panels that already exist -- the Mod Menu here and the Skin Settings panel in the
        // AS2-SkinSettings repo -- each arrived at separately and each held their own copy of. A
        // third mod would have had to measure them off a screenshot or read them out of somebody
        // else's source, and the two copies had already begun to differ.

        /// <summary>
        /// Margin down each side of the dialog's contents. The game's own rows start here.
        /// </summary>
        public const float SideMargin = 70f;

        /// <summary>Gap between the top of the dialog and the title</summary>
        public const float HeaderTop = 44f;

        /// <summary>Height of the title line, and of the dim subtitle under it</summary>
        public const float TitleHeight = 56f;
        public const float SubtitleHeight = 36f;

        /// <summary>Clear space between the header and whatever the panel puts below it</summary>
        public const float HeaderGap = 34f;

        /// <summary>The button row along the bottom</summary>
        // 60 units tall, top 100 above the dialog's bottom edge
        // Same baseline as the game's own Back/Next/OK, which EntryButtonRect sits on
        public const float FooterButtonHeight = 60f;
        public const float FooterButtonBaseline = 100f;

        /// <summary>Clear space between the body and the button row</summary>
        public const float FooterGap = 20f;

        /// <summary>
        /// Height to reserve below the body for a plain button row. A panel with more down there --
        /// a hover description, a standing note -- adds its own on top of this.
        /// </summary>
        public static float FooterHeight
        {
            get { return (FooterButtonBaseline + FooterGap) * Unit; }
        }

        /// <summary>The whole screen, for the backdrop behind a panel</summary>
        public static Rect FullScreen
        {
            get { return new Rect(0f, 0f, Screen.width, Screen.height); }
        }

        /// <summary>
        /// A rect spanning the dialog's contents at the given height, inset by <see cref="SideMargin"/>
        /// on both sides. The width every full-width thing in a panel should use.
        /// </summary>
        public static Rect ContentRect(Rect dialog, float y, float height)
        {
            float u = Unit;
            return new Rect(dialog.x + SideMargin * u, y, Mathf.Max(0f, dialog.width - 2f * SideMargin * u), height);
        }

        /// <summary>Height a <see cref="Header"/> occupies, trailing gap included</summary>
        // So a caller can lay out around it without drawing it first
        // Pass it straight to <see cref="BodyRect"/>
        public static float HeaderHeight(bool subtitled)
        {
            float u = Unit;
            return (HeaderTop + TitleHeight + (subtitled ? SubtitleHeight : 0f) + HeaderGap) * u;
        }

        /// <summary>A panel's title, and a dim second line under it when there is one</summary>
        // Returns the height it used, which is what <see cref="BodyRect"/> wants
        // Pass a blank subtitle for a title on its own -- the body then starts higher rather than
        // leaving a gap where a line would have been
        public static float Header(Rect dialog, string title, string subtitle)
        {
            if (NoGuiContext("AS2Ui.Header")) return HeaderHeight(!Str.IsBlank(subtitle));

            bool subtitled = !Str.IsBlank(subtitle);

            try
            {
                EnsureStyles();
                if (Title == null) return HeaderHeight(subtitled);

                float u = Unit;
                float y = dialog.y + HeaderTop * u;

                if (!Str.IsBlank(title))
                    GUI.Label(ContentRect(dialog, y, TitleHeight * u), title, Title);

                if (subtitled)
                    GUI.Label(ContentRect(dialog, y + TitleHeight * u, SubtitleHeight * u), subtitle, Dim);
            }
            catch (Exception ex) { WarnOnce("AS2Ui.Header could not draw: " + ex.Message); }

            return HeaderHeight(subtitled);
        }

        /// <summary>What is left of the dialog between a header and a footer</summary>
        // Full width, inset by <see cref="SideMargin"/>, never negative however small the window
        // Give the footer the height of everything below the body, buttons included
        // Nothing down there but the button row wants FooterHeight. A hover description or a
        // standing note adds its own height to that
        public static Rect BodyRect(Rect dialog, float headerHeight, float footerHeight)
        {
            float top = dialog.y + headerHeight;
            float bottom = dialog.yMax - footerHeight;
            return ContentRect(dialog, top, Mathf.Max(0f, bottom - top));
        }

        /// <summary>A button on the dialog's bottom row, against one side or the other</summary>
        // Width is in design units, like everything else here
        // 200 is what "Back" and "Close" draw at. 300 fits "Reset to defaults"
        public static Rect FooterButtonRect(Rect dialog, float widthUnits, bool fromRight)
        {
            float u = Unit;
            float w = widthUnits * u;
            float x = fromRight ? dialog.xMax - SideMargin * u - w : dialog.x + SideMargin * u;
            return new Rect(x, dialog.yMax - FooterButtonBaseline * u, w, FooterButtonHeight * u);
        }

        // ---- Row geometry -----------------------------------------------------------------------
        //
        // The columns the game lays a settings row out on, measured off the real dialog at 2560x1440
        // and expressed in design units from the row's left edge. Multiply by Unit, or just use
        // RowRects. These were prose on a wiki page before they were code, which
        // meant every mod that wanted a vanilla-looking row copied the numbers out of the prose and
        // owned its own drifting copy of them.

        /// <summary>Right edge of the label column. Labels are right-aligned to it</summary>
        public const float LabelColumnRight = 814f;

        /// <summary>Left edge of the control column, where a slider or checkbox starts</summary>
        public const float ControlColumnX = 838f;

        /// <summary>Width of the control column</summary>
        public const float ControlColumnWidth = 350f;

        /// <summary>Left edge of the value readout, e.g. "100%" or "Ultra"</summary>
        public const float ValueColumnX = 1206f;

        /// <summary>Distance from one row's top to the next. The game's rows are 83 apart</summary>
        public const float RowPitch = 83f;

        /// <summary>Splits a row into the three rects the game's settings dialog uses</summary>
        // Right-aligned label, the control, then the value readout to its right
        // Pass the full-width row. Everything derives from its x and its height, so this works the
        // same inside a scroll view as against the dialog
        public static void RowRects(Rect row, out Rect label, out Rect control, out Rect value)
        {
            float u = Unit;
            label   = new Rect(row.x, row.y, LabelColumnRight * u, row.height);
            control = new Rect(row.x + ControlColumnX * u, row.y, ControlColumnWidth * u, row.height);
            value   = new Rect(row.x + ValueColumnX * u, row.y, Mathf.Max(0f, row.width - ValueColumnX * u), row.height);
        }

        /// <summary>The n-th row of a list, <see cref="RowPitch"/> apart</summary>
        // Laid out from the top left of the rect you are filling
        // Inside a scroll view that rect is the content rect, whose origin is 0,0 -- which is why
        // the columns above measure from the row's own x rather than from the dialog's
        public static Rect Row(Rect area, int index)
        {
            float h = RowPitch * Unit;
            return new Rect(area.x, area.y + index * h, area.width, h);
        }

        /// <summary>Height a list of that many rows needs. The content height for a scroll view</summary>
        public static float RowsHeight(int rows)
        {
            return Mathf.Max(0, rows) * RowPitch * Unit;
        }

        // ---- Settings dialog state ------------------------------------------------------------

        private static FieldInfo _dialogOpenField;
        private static bool _dialogFieldResolved;

        /// <summary>Whether the game's settings dialog is on screen</summary>
        // Read from the game's own Settings.dialogOpen static, so it is right even when a mod loads
        // while the dialog is already up
        // <see cref="AS2Events.SettingsDialogToggled"/> is the event form
        public static bool SettingsDialogOpen
        {
            get
            {
                if (!_dialogFieldResolved)
                {
                    _dialogFieldResolved = true;
                    Type t = AccessTools.TypeByName("Settings");
                    if (t != null) _dialogOpenField = AccessTools.Field(t, "dialogOpen");
                    if (_dialogOpenField == null)
                        ModApiPlugin.Log.LogWarning("Settings.dialogOpen not found; AS2Ui.SettingsDialogOpen will always be false.");
                }

                if (_dialogOpenField == null) return false;
                try { return (bool)_dialogOpenField.GetValue(null); }
                catch { return false; }
            }
        }

        // ---- Never-throw plumbing ---------------------------------------------------------------
        //
        // Everything public below draws, and drawing is where this assembly is least in control of
        // its caller. AS2Ui is public API -- the README points mods at it -- so these run inside
        // somebody else's OnGUI, or, when the caller has misread the docs, somewhere that is not
        // OnGUI at all. The rest of the framework already holds the line that a mod which throws
        // must not take the game down; AS2ModMenu was only covered because ModApiPlugin.OnGUI wraps
        // the whole draw, which does nothing for a mod calling Button directly. So each entry point
        // guards, catches, logs once, and returns something the caller can carry on with.

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Logs a message the first time it is seen and drops every repeat</summary>
        // OnGUI runs several times per frame, so a failure here is never one event -- it is the
        // same line a few hundred times a second
        // Plain logging buries the rest of LogOutput.log moments after the first fault, and the log
        // is how anybody diagnoses this framework
        private static void WarnOnce(string message)
        {
            try
            {
                lock (Warned) { if (!Warned.Add(message)) return; }
                ModApiPlugin.Log.LogWarning(message);
            }
            catch { /* a logger that can break drawing is worse than a missing warning */ }
        }

        /// <summary>Whether there is no IMGUI event to draw against, so the caller is not in OnGUI</summary>
        // The misuse a mod author actually commits. AS2Ui looks like an ordinary helper, so it gets
        // called from Update or a coroutine, where Event.current is null and every GUI call throws
        // Answering true returns before anything is drawn, and before any control id is claimed
        // Bailing out mid-control would shift the id sequence for whoever is drawing legitimately
        // and break their layout instead
        private static bool NoGuiContext(string caller)
        {
            if (Event.current != null) return false;
            WarnOnce(caller + " was called with no current IMGUI event, so it did nothing. "
                   + "AS2Ui may only be used from inside OnGUI.");
            return true;
        }

        // ---- Styles ----------------------------------------------------------------------------

        private static bool _built;
        private static int _builtWidth, _builtHeight;
        private static bool _fontSearched;
        private static Font _menuFont;
        private static Texture2D _white;

        /// <summary>The style for a hit area that draws nothing</summary>
        // A control whose look is already drawn by hand and needs GUI.Button only for the click
        // Built once and shared, because OnGUI runs once per IMGUI event, several times a frame
        // A new GUIStyle() in the call is not one allocation per control drawn but one per control
        // per event, and this file leaves no per-frame garbage elsewhere
        // It carries no state, so sharing it between callers is safe
        private static readonly GUIStyle Invisible = new GUIStyle();

        public static GUIStyle Label { get; private set; }
        public static GUIStyle LabelRight { get; private set; }
        public static GUIStyle LabelCentre { get; private set; }
        public static GUIStyle Value { get; private set; }
        public static GUIStyle Title { get; private set; }

        /// <summary>Dim, one line, clipped at the edge of its rect</summary>
        public static GUIStyle Dim { get; private set; }

        /// <summary>Dim and wrapping, top-aligned, for prose that genuinely runs to several lines</summary>
        public static GUIStyle DimWrap { get; private set; }

        /// <summary>Builds the styles. Call at the top of OnGUI</summary>
        // GUIStyle construction is only legal inside OnGUI, which is why this is not in Awake
        // Rebuilt whenever the resolution changes, because a font size is in pixels and everything
        // else here is in design units
        // Building once froze the fonts at their old size while every rect around them resized
        // Not an edge case: the resolution slider lives in the dialog this draws into, and changing
        // it made labels overprint each other and button text fill its button edge to edge
        public static void EnsureStyles()
        {
            if (_built && _builtWidth == Screen.width && _builtHeight == Screen.height) return;
            if (NoGuiContext("AS2Ui.EnsureStyles")) return;

            try { BuildStyles(); }
            catch (Exception ex)
            {
                // Deliberately leaves _built false so the next frame tries again. The alternative,
                // marking the styles built on the way in, is what the previous version did: one
                // throw partway through left half the styles null and every later call took the
                // early return above, so the panel stayed broken for the rest of the session.
                WarnOnce("AS2Ui could not build its styles: " + ex.Message);
            }
        }

        private static void BuildStyles()
        {
            _white = Texture2D.whiteTexture;

            // The font itself does not change with the resolution, and the search walks every
            // loaded object, so it stays a one-off.
            if (!_fontSearched)
            {
                _fontSearched = true;
                _menuFont = FindMenuFont();
            }

            Label = new GUIStyle(GUI.skin.label);
            Label.font = _menuFont;
            Label.fontSize = FontSize(34f);
            Label.normal.textColor = TextColor;
            Label.alignment = TextAnchor.MiddleLeft;
            Label.wordWrap = false;
            Label.padding = new RectOffset(0, 0, 0, 0);

            LabelRight = new GUIStyle(Label);
            LabelRight.alignment = TextAnchor.MiddleRight;

            LabelCentre = new GUIStyle(Label);
            LabelCentre.alignment = TextAnchor.MiddleCenter;

            Value = new GUIStyle(Label);
            Value.alignment = TextAnchor.MiddleLeft;

            Title = new GUIStyle(Label);
            Title.fontSize = FontSize(42f);

            Dim = new GUIStyle(Label);
            Dim.fontSize = FontSize(26f);
            Dim.normal.textColor = DimText;

            DimWrap = new GUIStyle(Dim);
            DimWrap.alignment = TextAnchor.UpperLeft;
            DimWrap.wordWrap = true;

            // Last, so that these only claim "built at this size" once every style above exists.
            _builtWidth = Screen.width;
            _builtHeight = Screen.height;
            _built = true;
        }

        /// <summary>A design-space font size in pixels, with a floor</summary>
        // 800x600 puts the dim face at 8px, and below that it is unreadable anyway
        // A rounded-down 0 makes Unity fall back to the font's own size, far larger than asked for
        private static int FontSize(float designUnits)
        {
            return Mathf.Max(8, Mathf.RoundToInt(designUnits * Unit));
        }

        /// <summary>
        /// Best-effort: reuse the font the game's own menus are drawn with so the text matches
        /// rather than merely lining up. Falls back to the IMGUI default, which still reads fine.
        /// </summary>
        private static Font FindMenuFont()
        {
            try
            {
                Font best = null;
                foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (f == null || Str.IsBlank(f.name)) continue;
                    if (f.name.IndexOf("Arial", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    // Prefer whatever the menus use; the game ships a single squarish display face.
                    if (best == null || f.name.Length < best.name.Length) best = f;
                }

                if (best != null) ModApiPlugin.Log.LogInfo("AS2Ui using game font '" + best.name + "'.");
                return best;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not enumerate game fonts: " + e.Message);
                return null;
            }
        }

        // ---- Measuring text -----------------------------------------------------------------------

        /// <summary>One GUIContent, refilled for each measurement</summary>
        // CalcHeight keeps nothing, and OnGUI runs once per IMGUI event, so a fresh GUIContent per
        // call would be per-frame garbage the same way <see cref="Invisible"/> was
        private static readonly GUIContent Measured = new GUIContent();

        /// <summary>The height of one wrapped line in a style, at the width it will draw at</summary>
        // Use this and not GUIStyle.lineHeight to reserve room for a known number of lines
        // lineHeight is the font's line height. CalcHeight lays text out on the font's line spacing,
        // and the few units between the two clip the last line of an n-line paragraph
        // Measuring one line with the same call that measures the real text settles it
        public static float LineHeight(GUIStyle style, float width)
        {
            return TextHeight(style, "X", width);
        }

        /// <summary>
        /// How tall a string is in a style at a given width, wrapping included. Zero for blank text
        /// or a missing style, so a caller can add it to a layout without a null check.
        /// </summary>
        public static float TextHeight(GUIStyle style, string text, float width)
        {
            if (style == null || Str.IsBlank(text) || width <= 0f) return 0f;

            try
            {
                Measured.text = text;
                return style.CalcHeight(Measured, width);
            }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.TextHeight could not measure: " + ex.Message);
                return 0f;
            }
        }

        // ---- Primitives -------------------------------------------------------------------------

        /// <summary>Fills a rect</summary>
        // Colour restored in a finally, not after the draw
        // A throw between the two leaves GUI.color set and tints everything drawn afterwards by
        // anyone, which reads as a rendering bug in whichever mod drew next
        public static void Fill(Rect r, Color c)
        {
            if (NoGuiContext("AS2Ui.Fill")) return;

            Color previous = GUI.color;
            try
            {
                GUI.color = c;
                GUI.DrawTexture(r, _white ?? Texture2D.whiteTexture);
            }
            catch (Exception ex) { WarnOnce("AS2Ui.Fill could not draw: " + ex.Message); }
            finally { GUI.color = previous; }
        }

        /// <summary>Panel background plus the thin border the game's dialog has</summary>
        public static void Panel(Rect r)
        {
            if (NoGuiContext("AS2Ui.Panel")) return;

            try
            {
                Fill(r, PanelBg);
                float t = Mathf.Max(1f, Unit);
                Fill(new Rect(r.x, r.y, r.width, t), PanelBorder);
                Fill(new Rect(r.x, r.yMax - t, r.width, t), PanelBorder);
                Fill(new Rect(r.x, r.y, t, r.height), PanelBorder);
                Fill(new Rect(r.xMax - t, r.y, t, r.height), PanelBorder);
            }
            catch (Exception ex) { WarnOnce("AS2Ui.Panel could not draw: " + ex.Message); }
        }

        private static readonly int SliderHash = "AS2UiSlider".GetHashCode();

        /// <summary>The game's slider: one rail in two colours, with a tall block for the handle</summary>
        // White up to the handle, blue past it. Both halves are TrackThickness, the same
        // measurement the scrollbar's rail uses
        // Click or drag anywhere on the row to set the value
        // Hand-rolled rather than layered over GUI.HorizontalSlider: making the native one
        // invisible means an empty GUIStyle, which leaves a zero-width thumb and odd drag and
        // clamping behaviour. The hit test directly is a dozen lines and behaves predictably
        /// <summary>Handle size</summary>
        // Much taller than the rail, so it reads as a grip and not as a join between the colours
        public const float SliderHandleWidth = 15f;
        public const float SliderHandleHeight = 44f;

        public static float Slider(Rect r, float value, float min, float max)
        {
            if (NoGuiContext("AS2Ui.Slider")) return value;

            try { return SliderBody(r, value, min, max); }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.Slider failed: " + ex.Message);
                return value;
            }
        }

        private static float SliderBody(Rect r, float value, float min, float max)
        {
            float u = Unit;
            float handleW = Mathf.Max(2f, SliderHandleWidth * u);
            float usable = Mathf.Max(1f, r.width - handleW);
            float span = Mathf.Max(0.0001f, max - min);

            int id = GUIUtility.GetControlID(SliderHash, FocusType.Passive);
            Event e = Event.current;

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        value = ValueAt(e.mousePosition.x, r.x, handleW, usable, min, span);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        value = ValueAt(e.mousePosition.x, r.x, handleW, usable, min, span);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }

            float t = Mathf.Clamp01((value - min) / span);
            float handleX = r.x + usable * t;
            float split = handleX + handleW * 0.5f;

            float trackH = Mathf.Max(1f, TrackThickness * u);
            float handleH = SliderHandleHeight * u;
            float trackY = r.center.y - trackH * 0.5f;

            // Both halves are the same thickness: solid white up to the handle, solid blue past it.
            // An earlier attempt drew the white side as a hollow outlined channel, taller than the
            // blue, after reading it that way off a compressed screenshot. It is not -- the game
            // draws one rail in two colours, and the only thing the handle changes is where they
            // meet.
            Fill(new Rect(r.x, trackY, split - r.x, trackH), SliderFilled);
            Fill(new Rect(split, trackY, r.xMax - split, trackH), SliderRemainder);

            Fill(new Rect(handleX, r.center.y - handleH * 0.5f, handleW, handleH), SliderFilled);

            return value;
        }

        private static float ValueAt(float mouseX, float trackX, float handleW, float usable, float min, float span)
        {
            return min + Mathf.Clamp01((mouseX - trackX - handleW * 0.5f) / usable) * span;
        }

        /// <summary>The game's checkbox: a white square with a cross when set</summary>
        // The GUI matrix is restored in a finally. It rotates twice while drawing the cross, and a
        // throw between the rotation and the restore leaves every later control in the frame drawn
        // at 45 degrees -- a spectacular failure to pin on whichever mod drew next
        public static bool Toggle(Rect r, bool value)
        {
            if (NoGuiContext("AS2Ui.Toggle")) return value;

            Matrix4x4 matrix = GUI.matrix;
            try
            {
                float u = Unit;
                float box = 34f * u;
                var boxRect = new Rect(r.x, r.center.y - box * 0.5f, box, box);

                Fill(boxRect, TextColor);
                if (value)
                {
                    float inset = 7f * u;
                    var inner = new Rect(boxRect.x + inset, boxRect.y + inset, box - inset * 2f, box - inset * 2f);
                    float t = Mathf.Max(1f, 4f * u);
                    // Two bars rotated into a cross, drawn with the GUI matrix so it stays crisp.
                    GUIUtility.RotateAroundPivot(45f, inner.center);
                    Fill(new Rect(inner.x, inner.center.y - t * 0.5f, inner.width, t), PanelBg);
                    GUI.matrix = matrix;
                    GUIUtility.RotateAroundPivot(-45f, inner.center);
                    Fill(new Rect(inner.x, inner.center.y - t * 0.5f, inner.width, t), PanelBg);
                    GUI.matrix = matrix;
                }

                if (GUI.Button(boxRect, GUIContent.none, Invisible)) value = !value;
                return value;
            }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.Toggle failed: " + ex.Message);
                return value;
            }
            finally { GUI.matrix = matrix; }
        }

        /// <summary>A whole settings row for a boolean</summary>
        // Right-aligned label, then the game's checkbox in the control column
        // Returns the new value, so use it the way you would GUI.Toggle:
        //     myFlag = AS2Ui.ToggleRow(row, "Autofind Music", myFlag);
        // What the game does for Autofind Music, Vsync and the scoreboard options
        // Do not draw a boolean as a two-stop slider. The game has a checkbox, and a player reads a
        // slider that only moves between two positions as a broken slider
        // No value readout, matching the game -- the box is the readout
        public static bool ToggleRow(Rect row, string label, bool value)
        {
            if (NoGuiContext("AS2Ui.ToggleRow")) return value;

            try
            {
                EnsureStyles();

                Rect labelRect, control, unused;
                RowRects(row, out labelRect, out control, out unused);

                if (!Str.IsBlank(label) && LabelRight != null)
                    GUI.Label(labelRect, label, LabelRight);

                return Toggle(control, value);
            }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.ToggleRow failed: " + ex.Message);
                return value;
            }
        }

        /// <summary>A whole settings row for a number</summary>
        // Right-aligned label, the game's slider, then the value readout to its right
        //     volume = AS2Ui.SliderRow(row, "Music Volume", volume, 0f, 100f, percent + "%");
        // The readout is yours to format -- only you know whether it is a percentage, a count or a
        // multiplier
        // It describes the value passed in, not the value returned, so during a drag it trails the
        // handle by one IMGUI event. The game's own rows do the same, and it is not visible
        public static float SliderRow(Rect row, string label, float value, float min, float max, string valueText)
        {
            if (NoGuiContext("AS2Ui.SliderRow")) return value;

            try
            {
                EnsureStyles();

                Rect labelRect, control, valueRect;
                RowRects(row, out labelRect, out control, out valueRect);

                if (!Str.IsBlank(label) && LabelRight != null)
                    GUI.Label(labelRect, label, LabelRight);

                float result = Slider(control, value, min, max);

                if (!Str.IsBlank(valueText) && Value != null)
                    GUI.Label(valueRect, valueText, Value);

                return result;
            }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.SliderRow failed: " + ex.Message);
                return value;
            }
        }

        /// <summary>A whole settings row for a fixed set of options</summary>
        // A slider with one stop per option, and the chosen option's label where the number goes
        // Takes and returns an index:
        //     quality = AS2Ui.ChoiceRow(row, "Graphics Level", quality, new[] { "Low", "High", "Ultra" });
        // What the game does for Graphics Level, Anti-Aliasing and Resolution
        // Worth copying rather than drawing a list: it keeps every setting one row tall, so a panel
        // stays as short as the game's. A list of buttons turns 7 settings into something taller
        // than the screen
        // The readout follows the handle as it drags, so the label names the option it is about to
        // be set to
        public static int ChoiceRow(Rect row, string label, int index, string[] options)
        {
            if (NoGuiContext("AS2Ui.ChoiceRow")) return index;

            // No options is not a failure worth warning about -- a schema with an empty list is the
            // caller's data, not a mistake in this call -- but there is no row to draw either.
            if (options == null || options.Length == 0) return index;

            try
            {
                EnsureStyles();

                int count = options.Length;
                int current = Mathf.Clamp(index, 0, count - 1);

                Rect labelRect, control, valueRect;
                RowRects(row, out labelRect, out control, out valueRect);

                if (!Str.IsBlank(label) && LabelRight != null)
                    GUI.Label(labelRect, label, LabelRight);

                float raw = Slider(control, current, 0f, count - 1);
                int picked = Mathf.Clamp(Mathf.RoundToInt(raw), 0, count - 1);

                if (Value != null) GUI.Label(valueRect, options[picked] ?? "", Value);

                return picked;
            }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.ChoiceRow failed: " + ex.Message);
                return index;
            }
        }

        private static GUIStyle _buttonStyle;

        /// <summary>How much of a button's height its label may occupy</summary>
        // 60 > 34 -- the game's 60-unit button carries the 34-unit body face
        // The other 26 units are what reads as the button's padding, more below the text than above,
        // because a line box includes a descender these labels never use
        private const float LabelHeightFraction = 0.58f;

        /// <summary>The game's flat grey button</summary>
        // The label is fitted rather than clipped, in both directions
        // Height: a button shorter than the standard 60 units (the &lt; &gt; pair on the target
        // picker is 42) would have the body face fill it edge to edge, which is what makes a button
        // look unlike the game's
        // Width: the game's menu face is wide, so a label sized for body text overflows a generous
        // button easily. "Skin / Mode Settings" and "Reset to defaults" both lost their first and
        // last characters before this
        // Measuring here means no caller has to size its own rects
        public static bool Button(Rect r, string label, bool enabled = true)
        {
            if (NoGuiContext("AS2Ui.Button")) return false;

            try { return ButtonBody(r, label, enabled); }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.Button failed: " + ex.Message);
                return false;
            }
        }

        private static bool ButtonBody(Rect r, string label, bool enabled)
        {
            EnsureStyles();

            // EnsureStyles logs and gives up rather than throwing, so the styles can still be null
            // here on a build that failed. Drawing nothing beats an NRE out of the framework.
            if (Label == null) return false;

            bool hover = enabled && r.Contains(Event.current.mousePosition);
            Fill(r, !enabled ? ButtonBgDisabled : hover ? ButtonBgHover : ButtonBg);

            if (_buttonStyle == null)
            {
                _buttonStyle = new GUIStyle(Label);
                _buttonStyle.alignment = TextAnchor.MiddleCenter;
            }
            _buttonStyle.font = Label.font;
            _buttonStyle.normal.textColor = enabled ? TextColor : DimText;
            _buttonStyle.fontSize = Mathf.Max(8, Mathf.Min(Label.fontSize,
                                              Mathf.FloorToInt(r.height * LabelHeightFraction)));

            if (!Str.IsBlank(label))
            {
                float available = r.width - 28f * Unit;
                var content = new GUIContent(label);
                float needed = _buttonStyle.CalcSize(content).x;
                if (needed > available && needed > 0f)
                    _buttonStyle.fontSize = Mathf.Max(8, Mathf.FloorToInt(_buttonStyle.fontSize * available / needed));

                GUI.Label(r, content, _buttonStyle);
            }

            if (!enabled) return false;
            return GUI.Button(r, GUIContent.none, Invisible);
        }

        // ---- Scrolling ------------------------------------------------------------------------------
        //
        // Unity's scrollbars are the default IMGUI skin's and belong to no game in particular, so a
        // panel that wants to look like the settings dialog cannot use them and has to draw its own.
        // That is a page of input handling for something every scrolling panel needs identically,
        // which is exactly the kind of thing this class exists to stop each mod writing again.

        /// <summary>Strip kept clear at the right of a list for the scrollbar to ride in</summary>
        public const float ScrollGutter = 44f;

        /// <summary>Side of the scrollbar's thumb</summary>
        // Square, and the same size however long the list is, because that is what the game draws
        // A proportional thumb is more informative and looks nothing like it -- on a list one row
        // too tall it covers almost the whole track
        public const float ScrollThumb = 28f;

        /// <summary>Thickness of the rail a handle travels along</summary>
        // The scrollbar track's width and the slider track's height. One measurement on the game's
        // dialog, so one constant here
        // Much thinner than ScrollThumb on purpose. Drawing the track at the thumb's width, which
        // this used to do, gives a column with a slightly paler square sliding down it, and reads
        // as a progress bar
        // The thumb has to overhang the rail for the shape to say "handle"
        public const float TrackThickness = 12f;

        private static readonly int ScrollbarHash = "AS2UiScrollbar".GetHashCode();

        /// <summary>Whose turn the one shared scroll view is</summary>
        // IMGUI keeps one clip stack, so one view can be open however many mods draw
        // #38 -- that makes the bookkeeping shared state, and a mod whose OnGUI returned between
        // BeginScroll and EndScroll cost every mod drawn after it its own scroll view
        // ScrollTurns holds the rules away from Unity, so tests\AS2.ModApi.Tests drives them cold
        // The caller token is the mod's own assembly, which is what lets a warning name the mod
        // that left a view open
        private static readonly ScrollTurns Turns = new ScrollTurns();

        /// <summary>
        /// The body and the content height <see cref="EndScroll"/> measures the scrollbar against,
        /// so the bar lands beside the same body the open call was given.
        /// </summary>
        private static Rect _scrollBody;
        private static float _scrollContent;

        /// <summary>Opens a scrolling list over <paramref name="body"/> and returns the row rect</summary>
        // Close it with <see cref="EndScroll"/>, which draws the scrollbar:
        //     Rect content = AS2Ui.BeginScroll(body, ref _scroll, AS2Ui.RowsHeight(items.Count));
        //     for (int i = 0; i &lt; items.Count; i++) DrawRow(AS2Ui.Row(content, i));
        //     AS2Ui.EndScroll(ref _scroll);
        // The returned rect starts at 0,0, because coordinates inside a scroll view are relative to
        // it. That is also why the column constants measure from a row's own x
        // Narrower than the body by ScrollGutter, leaving the bar somewhere to ride
        // The view is the full width of the body on purpose. Size it to the rows and a horizontal
        // scrollbar gets in: EndScrollView clamps the horizontal offset to (view - visible), which
        // goes negative once the view is narrower, and one wheel tick shunts the list sideways
        // Equal widths make the horizontal range exactly zero
        [MethodImpl(MethodImplOptions.NoInlining)]   // GetCallingAssembly must see the mod, not us
        public static Rect BeginScroll(Rect body, ref Vector2 scroll, float contentHeight)
        {
            float gutter = ScrollGutter * Unit;
            var rows = new Rect(0f, 0f, Mathf.Max(0f, body.width - gutter), Mathf.Max(0f, contentHeight));

            if (NoGuiContext("AS2Ui.BeginScroll")) return rows;

            // Who is asking, so that a view left open can be attributed to the mod that left it, and
            // so that one mod's leak cannot refuse the next mod a scroll view. See ScrollTurns.
            ScrollBegin turn = Turns.Begin(Assembly.GetCallingAssembly(), Time.frameCount);
            if (turn.Warning != null) WarnOnce(turn.Warning);
            if (!turn.Open) return rows;

            bool opened = false;

            try
            {
                scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, contentHeight - body.height));

                // GUIStyle.none for both bars: Unity's vertical one is the wrong skin, and its
                // horizontal one used to appear uninvited at small window sizes, where a fixed 15px
                // vertical bar narrowed the view by more than the gutter -- which scales with the
                // resolution -- had set aside.
                scroll = GUI.BeginScrollView(body, scroll, new Rect(0f, 0f, body.width, rows.height),
                                             GUIStyle.none, GUIStyle.none);
                scroll.x = 0f;

                opened = true;
                _scrollBody = body;
                _scrollContent = rows.height;
            }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.BeginScroll failed: " + ex.Message);
            }

            // Told either way: EndScroll must not call GUI.EndScrollView for a view that never
            // opened, or Unity logs about the imbalance for the rest of the session.
            Turns.Opened(opened);

            return rows;
        }

        /// <summary>
        /// Closes the list <see cref="BeginScroll"/> opened and draws the scrollbar beside it,
        /// updating <paramref name="scroll"/> when the player drags the thumb.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]   // GetCallingAssembly must see the mod, not us
        public static void EndScroll(ref Vector2 scroll)
        {
            ScrollEnd turn = Turns.End(Assembly.GetCallingAssembly());
            if (turn.Warning != null) WarnOnce(turn.Warning);
            if (!turn.Close) return;

            try
            {
                GUI.EndScrollView();

                float gutter = ScrollGutter * Unit;
                var track = new Rect(_scrollBody.xMax - gutter, _scrollBody.y, gutter, _scrollBody.height);
                scroll.y = Scrollbar(track, scroll.y, _scrollContent);
            }
            catch (Exception ex) { WarnOnce("AS2Ui.EndScroll failed: " + ex.Message); }
        }

        /// <summary>The game's vertical scrollbar, drawn rather than styled</summary>
        // A pale square thumb on a track a shade lighter than the panel behind it
        // Returns the new scroll offset
        // Nothing drawn and no control claimed when the content already fits. The offset comes back
        // as 0, since no other value is in range
        // Clicking anywhere in the track jumps the thumb to the cursor and starts a drag, which is
        // how <see cref="Slider"/> and the game's own lists behave
        public static float Scrollbar(Rect track, float scrollY, float contentHeight)
        {
            if (NoGuiContext("AS2Ui.Scrollbar")) return scrollY;

            try
            {
                float hidden = contentHeight - track.height;
                if (hidden <= 0f) return 0f;

                float u = Unit;
                float size = Mathf.Max(4f, ScrollThumb * u);
                float rail = Mathf.Max(2f, TrackThickness * u);
                float travel = Mathf.Max(1f, track.height - size);

                // Rail and thumb share a centre line; the thumb overhangs on both sides. The hit
                // test below still uses the whole gutter, so a thin rail costs no click target.
                float thumbX = track.x + (track.width - size) * 0.5f;
                float railX = track.x + (track.width - rail) * 0.5f;

                int id = GUIUtility.GetControlID(ScrollbarHash, FocusType.Passive);
                Event e = Event.current;

                switch (e.GetTypeForControl(id))
                {
                    case EventType.MouseDown:
                        if (e.button == 0 && track.Contains(e.mousePosition))
                        {
                            GUIUtility.hotControl = id;
                            scrollY = ScrollAt(e.mousePosition.y, track.y, size, travel, hidden);
                            e.Use();
                        }
                        break;

                    case EventType.MouseDrag:
                        if (GUIUtility.hotControl == id)
                        {
                            scrollY = ScrollAt(e.mousePosition.y, track.y, size, travel, hidden);
                            e.Use();
                        }
                        break;

                    case EventType.MouseUp:
                        if (GUIUtility.hotControl == id)
                        {
                            GUIUtility.hotControl = 0;
                            e.Use();
                        }
                        break;
                }

                Fill(new Rect(railX, track.y, rail, track.height), PanelBorder);

                float t = Mathf.Clamp01(scrollY / hidden);
                Fill(new Rect(thumbX, track.y + travel * t, size, size), TextColor);

                return scrollY;
            }
            catch (Exception ex)
            {
                WarnOnce("AS2Ui.Scrollbar failed: " + ex.Message);
                return scrollY;
            }
        }

        private static float ScrollAt(float mouseY, float trackY, float size, float travel, float hidden)
        {
            return Mathf.Clamp01((mouseY - trackY - size * 0.5f) / travel) * hidden;
        }
    }
}
