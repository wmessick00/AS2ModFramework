using System;
using System.Collections.Generic;

namespace AS2.ModApi
{
    /// <summary>One mod's entry in the Mod Menu</summary>
    public sealed class ModMenuEntry
    {
        /// <summary>What the menu lists. The title the mod asked for, unless another mod already had it</summary>
        public string Title;
        public string Description;
        public Action Open;

        // Who registered it, and the title it asked for, which is not always the one it is listed
        // under. The registry matches a reload and an Unregister against these two together
        // #81
        internal string Owner;
        internal string Requested;
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
        /// Adds an entry. Registering the same title twice from the same mod replaces the first, so
        /// a plugin that reloads does not end up listed twice. Safe to call from any thread.
        /// </summary>
        // <paramref name="owner"/> is the assembly that called AS2ModMenu.Register, and what separates
        // a reload from a collision. This used to match on the title alone, so two unrelated mods
        // that each called their entry "Settings" were one entry: the second silently evicted the
        // first, which stayed gone until the game restarted and load order happened to change
        // #81
        // The same owner is a reload and replaces. A different owner is a collision, and it is not
        // allowed to replace anyone: the later mod is listed as "Title (Owner)", which keeps both
        // reachable and says in the log what happened. Only when even that is taken is it refused
        internal static void Register(string title, string description, Action open, string owner = null)
        {
            if (Str.IsBlank(title) || open == null)
            {
                ModApiPlugin.Log.LogWarning("Ignored a Mod Menu entry with no title or no action.");
                return;
            }

            string who = owner ?? "";
            string shown = title;
            string note = null;
            bool refused = false;

            lock (Gate)
            {
                ModMenuEntry mine = Requested(title, who);
                ModMenuEntry rival = Listed(shown, mine);

                if (rival != null)
                {
                    shown = title + " (" + Name(who) + ")";

                    if (Listed(shown, mine) != null)
                    {
                        refused = true;
                        note = "Mod Menu entry '" + title + "' from " + Name(who) + " was not added: '"
                             + title + "' is taken by " + Name(rival.Owner) + ", and so is '" + shown + "'.";
                    }
                    else
                    {
                        note = "Mod Menu entry '" + title + "' from " + Name(who) + " collides with the one "
                             + "from " + Name(rival.Owner) + ", so it is listed as '" + shown + "'.";
                    }
                }

                if (!refused)
                {
                    if (mine != null) Entries.Remove(mine);

                    Entries.Add(new ModMenuEntry
                    {
                        Title = shown, Description = description, Open = open, Owner = who, Requested = title
                    });
                    Entries.Sort(delegate (ModMenuEntry a, ModMenuEntry b)
                    {
                        return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                    });
                    Publish();
                }
            }

            // Logged outside the lock. Writing a log line is BepInEx's code taking BepInEx's locks,
            // and nothing here is worth holding Gate across a call into another component.
            if (note != null) ModApiPlugin.Log.LogWarning(note);
            if (!refused) ModApiPlugin.Log.LogInfo("Mod Menu entry registered: " + shown);
        }

        /// <summary>
        /// Removes the calling mod's entry by the title it registered under. Safe to call from any
        /// thread. Another mod's entry with the same title is not this one's to remove.
        /// </summary>
        internal static void Unregister(string title, string owner = null)
        {
            lock (Gate)
            {
                ModMenuEntry mine = Requested(title, owner ?? "");
                if (mine == null) return;

                Entries.Remove(mine);
                Publish();
            }
        }

        /// <summary>The entry this owner registered under this title, or null. The caller holds Gate</summary>
        private static ModMenuEntry Requested(string title, string owner)
        {
            foreach (ModMenuEntry e in Entries)
                if (string.Equals(e.Requested, title, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Owner, owner, StringComparison.Ordinal))
                    return e;

            return null;
        }

        /// <summary>
        /// The entry listed under this title, other than <paramref name="except"/>, or null. The
        /// caller holds Gate
        /// </summary>
        private static ModMenuEntry Listed(string shown, ModMenuEntry except)
        {
            foreach (ModMenuEntry e in Entries)
                if (!ReferenceEquals(e, except) && string.Equals(e.Title, shown, StringComparison.OrdinalIgnoreCase))
                    return e;

            return null;
        }

        /// <summary>How a mod is named in a message and in a listed title</summary>
        private static string Name(string owner)
        {
            return Str.IsBlank(owner) ? "an unknown mod" : owner;
        }

        /// <summary>Hands the drawing code a fresh snapshot. The caller holds Gate</summary>
        private static void Publish()
        {
            Published = Entries.ToArray();
        }
    }
}
