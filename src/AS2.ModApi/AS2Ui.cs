using System;
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

        // ---- Settings dialog state ------------------------------------------------------------

        private static FieldInfo _dialogOpenField;
        private static bool _dialogFieldResolved;

        /// <summary>
        /// Whether the game's settings dialog is on screen.
        ///
        /// Read from the game's own `Settings.dialogOpen` static, so it is correct even if a mod
        /// loads while the dialog is already up. <see cref="AS2Events.SettingsDialogOpened"/> is the
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

        // ---- Styles ----------------------------------------------------------------------------

        private static bool _built;
        private static int _builtWidth, _builtHeight;
        private static bool _fontSearched;
        private static Font _menuFont;
        private static Texture2D _white;

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
            _built = true;
            _builtWidth = Screen.width;
            _builtHeight = Screen.height;

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

        public static void Fill(Rect r, Color c)
        {
            Color previous = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white ?? Texture2D.whiteTexture);
            GUI.color = previous;
        }

        /// <summary>Panel background plus the thin border the game's dialog has.</summary>
        public static void Panel(Rect r)
        {
            Fill(r, PanelBg);
            float t = Mathf.Max(1f, Unit);
            Fill(new Rect(r.x, r.y, r.width, t), PanelBorder);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), PanelBorder);
            Fill(new Rect(r.x, r.y, t, r.height), PanelBorder);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), PanelBorder);
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

        /// <summary>The game's checkbox: a white square with a cross when set.</summary>
        public static bool Toggle(Rect r, bool value)
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
                Matrix4x4 m = GUI.matrix;
                GUIUtility.RotateAroundPivot(45f, inner.center);
                Fill(new Rect(inner.x, inner.center.y - t * 0.5f, inner.width, t), PanelBg);
                GUI.matrix = m;
                GUIUtility.RotateAroundPivot(-45f, inner.center);
                Fill(new Rect(inner.x, inner.center.y - t * 0.5f, inner.width, t), PanelBg);
                GUI.matrix = m;
            }

            if (GUI.Button(boxRect, GUIContent.none, new GUIStyle())) value = !value;
            return value;
        }

        private static GUIStyle _buttonStyle;
        private static readonly GUIStyle Invisible = new GUIStyle();

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
            EnsureStyles();

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
