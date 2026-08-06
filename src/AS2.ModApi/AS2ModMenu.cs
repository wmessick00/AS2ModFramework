using System;
using System.Collections.Generic;
using UnityEngine;

namespace AS2.ModApi
{
    /// <summary>One mod's entry in the Mod Menu.</summary>
    public sealed class ModMenuEntry
    {
        public string Title;
        public string Description;
        public Action Open;
    }

    /// <summary>
    /// A single "Mod Menu" button inside the game's settings dialog, and the hub it opens.
    ///
    /// This belongs to the framework rather than to any one mod. The settings dialog has room for
    /// exactly one extra button; if every mod added its own the row would overflow after the second,
    /// and the ordering would depend on plugin load order. So the API owns the button, and mods
    /// register an entry:
    ///
    ///     AS2ModMenu.Register("Skin and Mode Settings", "Configure skins and modes.", OpenMyPanel);
    ///
    /// Entries are listed alphabetically so the menu does not reshuffle when load order changes.
    /// </summary>
    public static class AS2ModMenu
    {
        private static readonly List<ModMenuEntry> Entries = new List<ModMenuEntry>();
        private static IDisposable _inputLock;
        private static Vector2 _scroll;

        /// <summary>Whether the hub itself is on screen.</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>
        /// Adds an entry. Registering the same title twice replaces the first, so a plugin that
        /// reloads does not end up listed twice.
        /// </summary>
        public static void Register(string title, string description, Action open)
        {
            if (Str.IsBlank(title) || open == null)
            {
                ModApiPlugin.Log.LogWarning("Ignored a Mod Menu entry with no title or no action.");
                return;
            }

            Unregister(title);
            Entries.Add(new ModMenuEntry { Title = title, Description = description, Open = open });
            Entries.Sort(delegate (ModMenuEntry a, ModMenuEntry b)
            {
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            ModApiPlugin.Log.LogInfo("Mod Menu entry registered: " + title);
        }

        public static void Unregister(string title)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (string.Equals(Entries[i].Title, title, StringComparison.OrdinalIgnoreCase))
                    Entries.RemoveAt(i);
        }

        public static void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            _scroll = Vector2.zero;
            _inputLock = AS2Input.Lock();
        }

        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (_inputLock != null) { _inputLock.Dispose(); _inputLock = null; }
        }

        /// <summary>Called from the API plugin's OnGUI. Mods should not call this.</summary>
        internal static void Draw()
        {
            if (IsOpen) { DrawHub(); return; }

            // The button only exists inside the game's own settings dialog.
            if (Entries.Count > 0 && AS2Ui.SettingsDialogOpen && AS2Ui.Button(AS2Ui.EntryButtonRect, "Mod Menu"))
                Open();
        }

        private static void DrawHub()
        {
            // A mod that opened its own panel from here has taken over the screen; stand aside.
            if (!AS2Ui.SettingsDialogOpen) { Close(); return; }

            Rect dialog = AS2Ui.DialogRect;
            float u = AS2Ui.Unit;

            AS2Ui.Fill(new Rect(0f, 0f, Screen.width, Screen.height), AS2Ui.Backdrop);
            AS2Ui.Panel(dialog);

            GUI.Label(new Rect(dialog.x + 70f * u, dialog.y + 44f * u, dialog.width - 140f * u, 56f * u),
                      "Mod Menu", AS2Ui.Title);
            GUI.Label(new Rect(dialog.x + 70f * u, dialog.y + 102f * u, dialog.width - 140f * u, 36f * u),
                      Entries.Count + " installed mod" + (Entries.Count == 1 ? "" : "s"), AS2Ui.Dim);

            float top = dialog.y + 170f * u;
            float bottom = dialog.yMax - 120f * u;
            var body = new Rect(dialog.x + 70f * u, top, dialog.width - 140f * u, bottom - top);

            // Sized from what the two faces actually measure rather than from fixed offsets, so the
            // description cannot land on top of the title when the fonts do not scale exactly with
            // the row -- which is what a font size rounded to whole pixels guarantees at some point.
            float titleH = AS2Ui.Label.lineHeight;
            float descH = AS2Ui.Dim.lineHeight;
            float gap = 6f * u;

            float rowH = Mathf.Max(108f * u, titleH + gap + descH + 40f * u);
            var content = new Rect(0f, 0f, body.width - 24f * u, Entries.Count * rowH);
            _scroll = GUI.BeginScrollView(body, _scroll, content, false, false);

            for (int i = 0; i < Entries.Count; i++)
            {
                ModMenuEntry entry = Entries[i];
                var row = new Rect(0f, i * rowH, content.width, rowH - 12f * u);

                if (AS2Ui.Button(row, ""))
                {
                    Close();
                    try { entry.Open(); }
                    catch (Exception e) { ModApiPlugin.Log.LogError("Mod Menu entry '" + entry.Title + "' threw on open: " + e); }
                }

                bool described = !Str.IsBlank(entry.Description);
                float blockH = described ? titleH + gap + descH : titleH;
                float textX = row.x + 24f * u;
                float textW = row.width - 48f * u;
                float y = row.y + (row.height - blockH) * 0.5f;

                GUI.Label(new Rect(textX, y, textW, titleH), entry.Title, AS2Ui.Label);
                if (described)
                    GUI.Label(new Rect(textX, y + titleH + gap, textW, descH), entry.Description, AS2Ui.Dim);
            }

            GUI.EndScrollView();

            if (AS2Ui.Button(new Rect(dialog.xMax - 70f * u - 200f * u, dialog.yMax - 100f * u, 200f * u, 60f * u), "Close"))
                Close();

            Event e2 = Event.current;
            if (e2 != null && e2.type == EventType.KeyDown && e2.keyCode == KeyCode.Escape)
            {
                Close();
                e2.Use();
            }
        }
    }
}
