using System;
using System.Collections.Generic;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>Who owns a Mod Menu entry: a reload replaces, a collision does not</summary>
    // #81. Register matched on the title alone, so two unrelated mods that each called their entry
    // "Settings" were one entry. The second silently evicted the first, which stayed gone until the
    // game restarted and load order happened to change, and nothing was logged. The only thing the
    // title match was for is a plugin that reloads and must not be listed twice
    // What tells a reload from a collision is who is asking. The public API takes that from the
    // assembly that called it, and the registry takes it as a plain string, which is what lets this
    // check drive it without a second assembly
    // ModMenuRegistry has been under a racing check since #16. Nothing here races. This is about what
    // the list says once the dust has settled
    internal static class MenuChecks
    {
        private const string Prefix = "issue81-";

        internal static void Run()
        {
            AReloadReplacesItself();
            TwoModsWithOneTitleAreBothListed();
            ACollidingModIsToldAboutItInTheLog();
            ARealodedCollidingModStaysOneEntry();
            AnUnregisterIsOnlyTheCallersOwn();
            TheTitleIsFreeAgainOnceItsHolderLeaves();
            ACaseDifferenceIsStillACollision();
            ATitleThatCannotBeMadeUniqueIsRefusedAndSaid();
            RegistrationsWithNoOwnerBehaveAsTheyAlwaysDid();
            TheListStaysInTitleOrder();
        }

        private static void AReloadReplacesItself()
        {
            Action first = delegate { };
            Action second = delegate { };

            ModMenuRegistry.Register(Prefix + "reload", "v1", first, "Mod.A");
            ModMenuRegistry.Register(Prefix + "reload", "v2", second, "Mod.A");

            List<ModMenuEntry> listed = Listed();

            True("#81: a mod registering its own title again is still one entry", listed.Count == 1);
            True("#81: and it is the newer registration", listed[0].Description == "v2");
            True("#81: whose action is the newer one", listed[0].Open == second);
            True("#81: under the title it asked for", listed[0].Title == Prefix + "reload");

            Clear();
        }

        private static void TwoModsWithOneTitleAreBothListed()
        {
            Action a = delegate { };
            Action b = delegate { };

            ModMenuRegistry.Register(Prefix + "Settings", "from A", a, "Mod.A");
            ModMenuRegistry.Register(Prefix + "Settings", "from B", b, "Mod.B");

            List<ModMenuEntry> listed = Listed();

            True("#81: two mods with one title are two entries, not one", listed.Count == 2);

            ModMenuEntry first = Find(listed, "from A");
            ModMenuEntry second = Find(listed, "from B");

            True("#81: the first mod is still there", first != null);
            True("#81: and still has the title it asked for", first != null && first.Title == Prefix + "Settings");
            True("#81: with its own action", first != null && first.Open == a);
            True("#81: the second is listed beside it", second != null);
            True("#81: under a title that names its mod", second != null && second.Title == Prefix + "Settings (Mod.B)");
            True("#81: with its own action", second != null && second.Open == b);

            // Not cleared: the next check carries on from this state.
        }

        private static void ACollidingModIsToldAboutItInTheLog()
        {
            ModApiPlugin.Log.Clear();
            ModMenuRegistry.Register(Prefix + "Other", "from A", delegate { }, "Mod.A");
            ModMenuRegistry.Register(Prefix + "Other", "from C", delegate { }, "Mod.C");

            True("#81: the collision is logged", ModApiPlugin.Log.Mentions("collides"));
            True("#81: naming the mod that was there first", ModApiPlugin.Log.Mentions("Mod.A"));
            True("#81: and the mod that came second", ModApiPlugin.Log.Mentions("Mod.C"));
            True("#81: and what the second is listed as", ModApiPlugin.Log.Mentions(Prefix + "Other (Mod.C)"));

            ModMenuRegistry.Unregister(Prefix + "Other", "Mod.A");
            ModMenuRegistry.Unregister(Prefix + "Other", "Mod.C");
        }

        private static void ARealodedCollidingModStaysOneEntry()
        {
            // State from TwoModsWithOneTitleAreBothListed: A holds "Settings", B is "Settings (Mod.B)".
            ModMenuRegistry.Register(Prefix + "Settings", "from B, again", delegate { }, "Mod.B");

            List<ModMenuEntry> listed = Listed();

            True("#81: a mod that was renamed to avoid a collision and reloads is not listed twice",
                 listed.Count == 2);
            True("#81: it is the newer registration", Find(listed, "from B, again") != null);
            True("#81: and the old one is gone", Find(listed, "from B") == null);
        }

        private static void AnUnregisterIsOnlyTheCallersOwn()
        {
            ModMenuRegistry.Unregister(Prefix + "Settings", "Mod.Z");
            True("#81: a mod that never registered the title removes nothing", Listed().Count == 2);

            ModMenuRegistry.Unregister(Prefix + "Settings", "Mod.B");
            List<ModMenuEntry> listed = Listed();

            True("#81: unregistering the title takes the caller's entry", listed.Count == 1);
            True("#81: and not the other mod's, which has the same title", listed.Count == 1 && listed[0].Description == "from A");
            True("#81: the title asked for is what unregisters, not the one that was listed",
                 Find(listed, "from B, again") == null);
        }

        private static void TheTitleIsFreeAgainOnceItsHolderLeaves()
        {
            // State: only A holds "Settings".
            ModMenuRegistry.Unregister(Prefix + "Settings", "Mod.A");
            ModMenuRegistry.Register(Prefix + "Settings", "from B, third time", delegate { }, "Mod.B");

            List<ModMenuEntry> listed = Listed();

            True("#81: with the other mod gone the title is free", listed.Count == 1);
            True("#81: and the next mod gets it as asked", listed.Count == 1 && listed[0].Title == Prefix + "Settings");

            Clear();
        }

        private static void ACaseDifferenceIsStillACollision()
        {
            // The titles were compared without regard to case before, and a menu with "Settings" and
            // "SETTINGS" in it is the confusion this exists to prevent.
            ModMenuRegistry.Register(Prefix + "Case", "lower", delegate { }, "Mod.A");
            ModMenuRegistry.Register(Prefix.ToUpperInvariant() + "CASE", "upper", delegate { }, "Mod.B");

            True("#81: titles that differ only in case collide", Listed().Count == 2);
            True("#81: and the second is the one that is renamed",
                 Find(Listed(), "upper").Title == Prefix.ToUpperInvariant() + "CASE (Mod.B)");

            Clear();
        }

        private static void ATitleThatCannotBeMadeUniqueIsRefusedAndSaid()
        {
            // A holds the title, and a third mod already holds the name B would be listed under.
            ModMenuRegistry.Register(Prefix + "Taken", "from A", delegate { }, "Mod.A");
            ModMenuRegistry.Register(Prefix + "Taken (Mod.B)", "from D", delegate { }, "Mod.D");

            ModApiPlugin.Log.Clear();
            Action b = delegate { };
            ModMenuRegistry.Register(Prefix + "Taken", "from B", b, "Mod.B");

            List<ModMenuEntry> listed = Listed();

            True("#81: an entry that cannot be listed under any name is refused", Find(listed, "from B") == null);
            True("#81: and nobody else's is touched", listed.Count == 2);
            True("#81: the refusal is logged", ModApiPlugin.Log.Mentions("was not added"));
            False("#81: and is not reported as registered", ModApiPlugin.Log.Mentions("registered:"));

            Clear();
        }

        private static void RegistrationsWithNoOwnerBehaveAsTheyAlwaysDid()
        {
            // What the racing checks do, and what any caller that does not say who it is gets: one
            // owner, so the same title is the same entry.
            ModMenuRegistry.Register(Prefix + "anon", "one", delegate { });
            ModMenuRegistry.Register(Prefix + "anon", "two", delegate { });

            True("#81: with no owner named the same title is still the same entry", Listed().Count == 1);

            ModMenuRegistry.Unregister(Prefix + "anon");

            True("#81: and unregistering it by title still works", Listed().Count == 0);
        }

        private static void TheListStaysInTitleOrder()
        {
            ModMenuRegistry.Register(Prefix + "b", "b", delegate { }, "Mod.A");
            ModMenuRegistry.Register(Prefix + "a", "a", delegate { }, "Mod.B");
            ModMenuRegistry.Register(Prefix + "b", "b2", delegate { }, "Mod.C");

            List<ModMenuEntry> listed = Listed();

            bool ordered = true;
            for (int i = 1; i < listed.Count; i++)
                if (string.Compare(listed[i - 1].Title, listed[i].Title, StringComparison.OrdinalIgnoreCase) > 0)
                    ordered = false;

            True("#81: entries are listed alphabetically, renamed ones included", ordered);
            True("#81: all three are there", listed.Count == 3);

            Clear();
        }

        // ---- Fixtures -----------------------------------------------------------------------------

        /// <summary>The entries this file registered, whatever else is in the registry.</summary>
        private static List<ModMenuEntry> Listed()
        {
            var mine = new List<ModMenuEntry>();
            foreach (ModMenuEntry e in ModMenuRegistry.Snapshot)
                if (e.Title.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) mine.Add(e);

            return mine;
        }

        private static ModMenuEntry Find(List<ModMenuEntry> listed, string description)
        {
            foreach (ModMenuEntry e in listed)
                if (e.Description == description) return e;

            return null;
        }

        /// <summary>Takes out whatever this file left, so the next check starts from nothing.</summary>
        private static void Clear()
        {
            foreach (ModMenuEntry e in Listed())
                ModMenuRegistry.Unregister(e.Requested, e.Owner);
        }
    }
}
