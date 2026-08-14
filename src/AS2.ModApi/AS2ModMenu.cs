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
        /// <summary>
        /// The registration list, and the snapshot the drawing code reads.
        ///
        /// Mods call Register from wherever their own code runs, and nothing says that is the main
        /// thread: a plugin that registers from the callback of a web request or a file read is
        /// doing something ordinary. OnGUI walks the same entries every frame, and often more than
        /// once per frame. A List that another thread adds to during that walk throws "Collection
        /// was modified"; ModApiPlugin.OnGUI catches it and closes the menu, so one mod's
        /// registration timing would shut the shared hub on every other mod.
        ///
        /// So the two roles are separated. Entries is the master copy, and every change to it is
        /// made while holding Gate. Published is what the drawing code reads, replaced whole after
        /// each change and never modified in place. Assigning a reference cannot be seen half done,
        /// so a frame draws the list as it was before the change or as it is after it, and the
        /// draw path takes no lock at all.
        /// </summary>
        private static readonly object Gate = new object();
        private static readonly List<ModMenuEntry> Entries = new List<ModMenuEntry>();
        private static volatile ModMenuEntry[] Published = new ModMenuEntry[0];

        private static IDisposable _inputLock;
        private static Vector2 _scroll;

        /// <summary>Whether the hub itself is on screen.</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>
        /// Adds an entry. Registering the same title twice replaces the first, so a plugin that
        /// reloads does not end up listed twice. Safe to call from any thread.
        /// </summary>
        public static void Register(string title, string description, Action open)
        {
            if (Str.IsBlank(title) || open == null)
            {
                ModApiPlugin.Log.LogWarning("Ignored a Mod Menu entry with no title or no action.");
                return;
            }

            var entry = new ModMenuEntry { Title = title, Description = description, Open = open };

            lock (Gate)
            {
                RemoveTitle(title);
                Entries.Add(entry);
                Entries.Sort(delegate (ModMenuEntry a, ModMenuEntry b)
                {
                    return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                });
                Publish();
            }

            // Logged outside the lock. Writing a log line is BepInEx's code taking BepInEx's locks,
            // and nothing here is worth holding Gate across a call into another component.
            ModApiPlugin.Log.LogInfo("Mod Menu entry registered: " + title);
        }

        /// <summary>Removes an entry by title. Safe to call from any thread.</summary>
        public static void Unregister(string title)
        {
            lock (Gate)
            {
                if (RemoveTitle(title)) Publish();
            }
        }

        /// <summary>
        /// Drops every entry with this title and reports whether it dropped any. The caller holds
        /// Gate; this must not publish, because Register removes and adds as one change.
        /// </summary>
        private static bool RemoveTitle(string title)
        {
            bool removed = false;

            for (int i = Entries.Count - 1; i >= 0; i--)
                if (string.Equals(Entries[i].Title, title, StringComparison.OrdinalIgnoreCase))
                {
                    Entries.RemoveAt(i);
                    removed = true;
                }

            return removed;
        }

        /// <summary>Hands the drawing code a fresh snapshot. The caller holds Gate.</summary>
        private static void Publish()
        {
            Published = Entries.ToArray();
        }

        /// <summary>
        /// Opens the hub. Unlike Register, this belongs on the main thread: it takes the game's
        /// input lock, and the drawing state it sets is read by OnGUI.
        /// </summary>
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
            if (Published.Length > 0 && AS2Ui.SettingsDialogOpen && AS2Ui.Button(AS2Ui.EntryButtonRect, "Mod Menu"))
                Open();
        }

        private static void DrawHub()
        {
            // A mod that opened its own panel from here has taken over the screen; stand aside.
            if (!AS2Ui.SettingsDialogOpen) { Close(); return; }

            // One read of the snapshot for the whole frame. Reading Published again further down
            // would let a registration from another thread land between the count in the header and
            // the loop that draws the rows, and IMGUI replays this structure for the input and
            // Repaint events of the same frame -- so the count, the scroll content and the rows all
            // have to come from one list.
            ModMenuEntry[] entries = Published;

            Rect dialog = AS2Ui.DialogRect;
            float u = AS2Ui.Unit;

            AS2Ui.Fill(AS2Ui.FullScreen, AS2Ui.Backdrop);
            AS2Ui.Panel(dialog);

            // Every rect below comes from AS2Ui rather than from numbers of its own. The hub is the
            // framework's own panel, so it is also the working example a mod is entitled to copy:
            // anything it had to measure for itself would be a number the next mod has to measure
            // again, which is the whole reason these live in AS2Ui.
            float headerH = AS2Ui.Header(dialog, "Mod Menu",
                                         entries.Length + " installed mod" + (entries.Length == 1 ? "" : "s"));

            Rect body = AS2Ui.BodyRect(dialog, headerH, AS2Ui.FooterHeight);

            // Sized from what the two faces actually measure rather than from fixed offsets, so the
            // description cannot land on top of the title when the fonts do not scale exactly with
            // the row -- which is what a font size rounded to whole pixels guarantees at some point.
            // Not RowPitch: an entry is a two-line card, not a settings row.
            float titleH = AS2Ui.Label.lineHeight;
            float descH = AS2Ui.Dim.lineHeight;
            float gap = 6f * u;

            float rowH = Mathf.Max(108f * u, titleH + gap + descH + 40f * u);
            Rect content = AS2Ui.BeginScroll(body, ref _scroll, entries.Length * rowH);

            for (int i = 0; i < entries.Length; i++)
            {
                ModMenuEntry entry = entries[i];
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

            AS2Ui.EndScroll(ref _scroll);

            if (AS2Ui.Button(AS2Ui.FooterButtonRect(dialog, 200f, true), "Close"))
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
