using System;
using System.Collections.Generic;

namespace AS2.ModApi
{
    /// <summary>One mod's entry in the Mod Menu</summary>
    public sealed class ModMenuEntry
    {
        public string Title;
        public string Description;
        public Action Open;
    }

    /// <summary>Who is registered in the Mod Menu, and the snapshot the drawing code reads</summary>
    // Two roles, kept apart. Entries is the master copy and every change holds Gate
    // Snapshot is what the draw path reads: replaced whole, never modified in place, no lock taken
    // Assigning a reference cannot be seen half done, so a frame draws the list as it was before
    // the change or as it is after it
    // #16 -- a plain List that another thread added to mid-draw threw "Collection was modified",
    // ModApiPlugin.OnGUI caught it and closed the menu, so one mod's timing shut the hub on all
    // Apart from AS2ModMenu, which draws, so this file stays off BepInEx and Unity and the locking
    // stays coverable by a cold check
    internal static class ModMenuRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<ModMenuEntry> Entries = new List<ModMenuEntry>();
        private static volatile ModMenuEntry[] Published = new ModMenuEntry[0];

        /// <summary>The entries as they stood at the moment of reading</summary>
        // Never modified in place, so walk the array without holding anything and without copying
        // Read it once per frame and use that. Two reads can disagree, which is the same bug the
        // snapshot exists to remove
        internal static ModMenuEntry[] Snapshot { get { return Published; } }

        /// <summary>
        /// Adds an entry. Registering the same title twice replaces the first, so a plugin that
        /// reloads does not end up listed twice. Safe to call from any thread.
        /// </summary>
        internal static void Register(string title, string description, Action open)
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

        /// <summary>Removes an entry by title. Safe to call from any thread</summary>
        internal static void Unregister(string title)
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

        /// <summary>Hands the drawing code a fresh snapshot. The caller holds Gate</summary>
        private static void Publish()
        {
            Published = Entries.ToArray();
        }
    }
}
