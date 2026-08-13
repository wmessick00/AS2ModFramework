using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AS2.ModApi
{
    /// <summary>
    /// Draws IMGUI that looks like the game's own settings dialog, and tells you when that dialog is
    /// open so a mod can put its entry point inside it rather than floating over the game.
    ///
    /// The game's settings menu is EZGUI (`Settings : Menu`, built by `buildOriginalSettings` /
    /// `buildAS2InfoSettings`), not IMGUI, so it cannot simply be appended to: its rows come from
    /// prefabs via `Menu.setContents(Item[], int, float)`. Reproducing the look in IMGUI is both far
    /// less fragile and the only practical option for content that changes per skin.
    ///
    /// All geometry is expressed in the game's 1440p design space and scaled by
    /// <see cref="Unit"/>, so a value measured off a 2560x1440 screenshot can be typed in directly
    /// and still land correctly at any resolution. Positions are offsets from the centre of the
    /// screen or of <see cref="DialogRect"/> rather than from a screen edge, because the dialog is
    /// centred and everything in it moves with it.
    /// </summary>
    public static class AS2Ui
    {
        /// <summary>The design resolution everything below was measured against.</summary>
        public const float DesignWidth = 2560f;
        public const float DesignHeight = 1440f;

        // ---- Palette, sampled from the game's settings dialog --------------------------------

        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.60f);
        public static readonly Color PanelBg = new Color(0.043f, 0.043f, 0.043f, 1f);
        public static readonly Color PanelBorder = new Color(0.17f, 0.17f, 0.17f, 1f);
        public static readonly Color TextColor = Color.white;
        public static readonly Color DimText = new Color(0.62f, 0.62f, 0.62f, 1f);

        /// <summary>The track behind the handle. The game draws this white.</summary>
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

        /// <summary>
        /// Pixels per design unit. Multiply any measured 1440p value by this.
        ///
        /// The game scales its dialog on **width**, not height. At 1280x768 its dialog measures
        /// 858x608 -- 1716x1216 at exactly half scale -- and its rows sit 41.5px apart, where a
        /// height-derived scale would give 915x648 and a 44px pitch. Taking the smaller of the two
        /// ratios reproduces that on every aspect narrower than 16:9 and, on displays wider than
        /// 16:9, keeps the dialog on screen instead of letting a width-only scale make it taller
        /// than the window.
        /// </summary>
        public static float Unit
        {
            get { return Mathf.Min(Screen.width / DesignWidth, Screen.height / DesignHeight); }
        }

        /// <summary>Where the game centres its dialog: 1716 x 1216 design units, screen centred.</summary>
        public static Rect DialogRect
        {
            get
            {
                float u = Unit;
                float w = 1716f * u, h = 1216f * u;
                return new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            }
        }

        /// <summary>
        /// The empty space to the left of the dialog's Back/Next/OK row, which is where a mod's
        /// entry button belongs.
        ///
        /// Sits on the same baseline as Back/Next/OK (60 design units above the dialog's bottom
        /// edge) rather than floating above them, and is centred 666 design units left of the
        /// dialog's centre, in the gap between its left edge and the Back button.
        ///
        /// Anchored to <see cref="DialogRect"/> rather than to the screen: a y measured down from
        /// the top of the screen only holds at the design resolution, and at 1280x768 it put this
        /// button below the dialog's bottom edge and outside its left one.
        /// </summary>
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

        // ---- Row geometry -----------------------------------------------------------------------
        //
        // The columns the game lays a settings row out on, measured off the real dialog at 2560x1440
        // and expressed in design units from the row's left edge. Multiply by Unit, or just use
        // RowRects. These were documented in docs/game-internals.md before they were code, which
        // meant every mod that wanted a vanilla-looking row copied the numbers out of the prose and
        // owned its own drifting copy of them.

        /// <summary>Right edge of the label column. Labels are right-aligned to it.</summary>
        public const float LabelColumnRight = 814f;

        /// <summary>Left edge of the control column, where a slider or checkbox starts.</summary>
        public const float ControlColumnX = 838f;

        /// <summary>Width of the control column.</summary>
        public const float ControlColumnWidth = 350f;

        /// <summary>Left edge of the value readout, e.g. "100%" or "Ultra".</summary>
        public const float ValueColumnX = 1206f;

        /// <summary>Distance from one row's top to the next. The game's rows are 83 apart.</summary>
        public const float RowPitch = 83f;

        /// <summary>
        /// Splits a row into the three rects the game's own settings dialog uses: a right-aligned
        /// label, the control, and the value readout to its right.
        ///
        /// Pass the full-width row; everything is derived from its x and its height, so this works
        /// the same inside a scroll view as it does against the dialog.
        /// </summary>
        public static void RowRects(Rect row, out Rect label, out Rect control, out Rect value)
        {
            float u = Unit;
            label   = new Rect(row.x, row.y, LabelColumnRight * u, row.height);
            control = new Rect(row.x + ControlColumnX * u, row.y, ControlColumnWidth * u, row.height);
            value   = new Rect(row.x + ValueColumnX * u, row.y, Mathf.Max(0f, row.width - ValueColumnX * u), row.height);
        }

        // ---- Settings dialog state ------------------------------------------------------------

        private static FieldInfo _dialogOpenField;
        private static bool _dialogFieldResolved;

        /// <summary>
        /// Whether the game's settings dialog is on screen.
        ///
        /// Read from the game's own `Settings.dialogOpen` static, so it is correct even if a mod
        /// loads while the dialog is already up. <see cref="AS2Events.SettingsDialogToggled"/> is the
        /// event form.
        /// </summary>
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

        /// <summary>
        /// Logs a message the first time it is seen and drops every repeat.
        ///
        /// OnGUI runs several times per frame, so a failure here is never a single event: it is the
        /// same line a few hundred times a second. Plain logging would bury whatever else was in
        /// LogOutput.log within moments of the first fault, which is the opposite of useful when the
        /// log is how anybody diagnoses this framework.
        /// </summary>
        private static void WarnOnce(string message)
        {
            try
            {
                lock (Warned) { if (!Warned.Add(message)) return; }
                ModApiPlugin.Log.LogWarning(message);
            }
            catch { /* a logger that can break drawing is worse than a missing warning */ }
        }

        /// <summary>
        /// Whether there is no IMGUI event to draw against, meaning the caller is not inside OnGUI.
        ///
        /// This is the misuse a mod author actually commits: AS2Ui looks like an ordinary helper, so
        /// it gets called from Update or from a coroutine, where Event.current is null and every GUI
        /// call below would throw. Answering true returns before anything is drawn and, importantly,
        /// before any control id is claimed -- bailing out mid-control would shift the id sequence
        /// for whoever is drawing legitimately and break their layout instead.
        /// </summary>
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

        /// <summary>
        /// The style for a hit area that draws nothing: a control whose look is already drawn by
        /// hand, and which needs GUI.Button only for the click.
        ///
        /// Built once and shared, because OnGUI runs several times per frame -- once per IMGUI
        /// event. A `new GUIStyle()` in the call itself is therefore not one allocation per control
        /// drawn but one per control per event, and this file is otherwise careful to leave no
        /// per-frame garbage. It carries no state, so sharing it between callers is safe.
        /// </summary>
        private static readonly GUIStyle Invisible = new GUIStyle();

        public static GUIStyle Label { get; private set; }
        public static GUIStyle LabelRight { get; private set; }
        public static GUIStyle LabelCentre { get; private set; }
        public static GUIStyle Value { get; private set; }
        public static GUIStyle Title { get; private set; }

        /// <summary>Dim, one line, clipped at the edge of its rect.</summary>
        public static GUIStyle Dim { get; private set; }

        /// <summary>Dim and wrapping, top-aligned, for prose that genuinely runs to several lines.</summary>
        public static GUIStyle DimWrap { get; private set; }

        /// <summary>
        /// Builds the styles. Call at the top of OnGUI; GUIStyle construction is only legal inside
        /// OnGUI, which is why this is not done in Awake.
        ///
        /// Rebuilt whenever the resolution changes, because a font size is in pixels and everything
        /// else here is in design units. Building once left the fonts frozen at their old size while
        /// every rect around them resized -- and the resolution slider lives in the very dialog this
        /// draws into, so that was not an edge case: changing it made labels overprint each other
        /// and button text fill its button edge to edge.
        /// </summary>
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

        /// <summary>
        /// A design-space font size in pixels, with a floor: 800x600 puts the dim face at 8px and
        /// anything below that is unreadable anyway, while a rounded-down 0 makes Unity fall back to
        /// the font's own size, which is far larger than anything asked for here.
        /// </summary>
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

        // ---- Primitives -------------------------------------------------------------------------

        /// <summary>
        /// Fills a rect. The colour is restored in a finally rather than after the draw, because a
        /// throw between the two would otherwise leave GUI.color set and tint everything drawn
        /// afterwards by anyone, which reads as a rendering bug in whichever mod drew next.
        /// </summary>
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

        /// <summary>Panel background plus the thin border the game's dialog has.</summary>
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

        /// <summary>
        /// The game's slider: a thin track, white up to the handle and blue past it, with a tall
        /// white block for the handle. Click or drag anywhere on the row to set the value.
        ///
        /// The interaction is hand-rolled rather than layered over GUI.HorizontalSlider, because
        /// making the native one invisible means giving it an empty GUIStyle, which leaves it with a
        /// zero-width thumb and correspondingly odd drag and clamping behaviour. Doing the hit test
        /// directly is a dozen lines and behaves predictably.
        /// </summary>
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
            float handleW = Mathf.Max(2f, 9f * u);
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
            float trackH = Mathf.Max(1f, 3f * u);
            float handleH = 34f * u;
            float split = handleX + handleW * 0.5f;
            float trackY = r.center.y - trackH * 0.5f;

            Fill(new Rect(r.x, trackY, split - r.x, trackH), SliderFilled);
            Fill(new Rect(split, trackY, r.xMax - split, trackH), SliderRemainder);
            Fill(new Rect(handleX, r.center.y - handleH * 0.5f, handleW, handleH), SliderFilled);

            return value;
        }

        private static float ValueAt(float mouseX, float trackX, float handleW, float usable, float min, float span)
        {
            return min + Mathf.Clamp01((mouseX - trackX - handleW * 0.5f) / usable) * span;
        }

        /// <summary>
        /// The game's checkbox: a white square with a cross when set.
        ///
        /// The GUI matrix is restored in a finally. It is rotated twice while drawing the cross, and
        /// a throw between the rotation and the restore would leave every later control in the frame
        /// drawn at 45 degrees -- a spectacular failure to pin on whichever mod drew next.
        /// </summary>
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

        /// <summary>
        /// A whole settings row for a boolean: right-aligned label, then the game's checkbox in the
        /// control column. Returns the new value, so use it the way you would GUI.Toggle:
        ///
        ///     myFlag = AS2Ui.ToggleRow(row, "Autofind Music", myFlag);
        ///
        /// This is what the game does for Autofind Music, Vsync and the scoreboard options, and it
        /// is why a boolean should not be drawn as a two-stop slider: the game has a checkbox and a
        /// player reads a slider that only moves between two positions as a broken slider.
        ///
        /// No value readout, matching the game -- the box is the readout. Rows are
        /// <see cref="RowPitch"/> apart.
        /// </summary>
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

        private static GUIStyle _buttonStyle;

        /// <summary>
        /// How much of a button's height its label may occupy. The game's 60-unit button carries the
        /// 34-unit body face, and the space the other 26 units leave is what reads as the button's
        /// padding -- more of it below the text than above, because a line box includes a descender
        /// these labels never use.
        /// </summary>
        private const float LabelHeightFraction = 0.58f;

        /// <summary>
        /// The game's flat grey button.
        ///
        /// The label is fitted rather than clipped, in both directions. Height matters because a
        /// button shorter than the standard 60 units -- the &lt; &gt; pair on the target picker is 42 --
        /// would otherwise have the body face fill it edge to edge, which is what makes a button
        /// look unlike the game's. Width matters because the game's menu face is wide, so a label
        /// sized for the body text overflows a generously sized button surprisingly easily:
        /// "Skin / Mode Settings" and "Reset to defaults" both rendered with their first and last
        /// characters cut off before this. Measuring here means no caller has to size its own rects.
        /// </summary>
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
    }
}
