using System;
using System.Collections.Generic;

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
    /// Who is registered in the Mod Menu, and the snapshot the drawing code reads.
    ///
    /// <para>
    /// Mods call Register from wherever their own code runs, and nothing says that is the main
    /// thread: a plugin that registers from the callback of a web request or a file read is doing
    /// something ordinary. OnGUI walks the same entries every frame, and often more than once per
    /// frame. A List that another thread adds to during that walk throws "Collection was modified";
    /// ModApiPlugin.OnGUI catches it and closes the menu, so one mod's registration timing would
    /// shut the shared hub on every other mod. That shipped once, as issue #16.
    /// </para>
    ///
    /// <para>
    /// So the two roles are separated. <c>Entries</c> is the master copy, and every change to it is
    /// made while holding <c>Gate</c>. <see cref="Snapshot"/> is what the drawing code reads,
    /// replaced whole after each change and never modified in place. Assigning a reference cannot be
    /// seen half done, so a frame draws the list as it was before the change or as it is after it,
    /// and the draw path takes no lock at all.
    /// </para>
    ///
    /// <para>
    /// This lives apart from <see cref="AS2ModMenu"/>, which draws, for the same reason
    /// <see cref="Str"/> and <see cref="SelectorKind"/> live in their own files: every file on the
    /// cold test project's compile list has to stay free of BepInEx and Unity. The locking is the
    /// part worth a regression test and the part a reader cannot check by looking, and it needed no
    /// Unity to do its job -- so it does not sit behind any.
    /// </para>
    /// </summary>
    internal static class ModMenuRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<ModMenuEntry> Entries = new List<ModMenuEntry>();
        private static volatile ModMenuEntry[] Published = new ModMenuEntry[0];

        /// <summary>
        /// The entries as they stood at the moment of reading. Never modified in place, so a caller
        /// may walk the array it gets back without holding anything and without copying it.
        ///
        /// Read it once per frame and use that. Reading it twice invites the two reads to disagree,
        /// which is the same class of bug the snapshot exists to remove.
        /// </summary>
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

        /// <summary>Removes an entry by title. Safe to call from any thread.</summary>
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

        /// <summary>Hands the drawing code a fresh snapshot. The caller holds Gate.</summary>
        private static void Publish()
        {
            Published = Entries.ToArray();
        }
    }
}
