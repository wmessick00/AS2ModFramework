using System;
using System.Threading;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>Regression cover for the two concurrency bugs this framework shipped and fixed</summary>
    // #16 and #26. Neither had a check, which is the worst shape for a bug of this kind:
    //   -the fix is a few careful lines that look like style
    //   -a later reader has every reason to simplify them
    //   -the failure only appears on somebody else's machine, at a timing they cannot reproduce
    // TargetResolver's guards have had regression checks for #15, #2 and #13 for the same reason
    // A racing check cannot prove the absence of a race. It can drive the two threads hard enough
    // that the unfixed code fails almost every run, which is the honest bargain
    // Revert either fix and these go red. Both were run against the unfixed shape first
    internal static class ConcurrencyChecks
    {
        /// <summary>
        /// How long each racing check runs. Long enough that the unfixed code fails reliably, short
        /// enough that the suite stays something you run without thinking about it.
        /// </summary>
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(700);

        internal static void Run()
        {
            RegisteringWhileDrawingDoesNotThrow();
            SnapshotIsNeverSeenHalfWritten();
            UnsubscribingWhileRaisingDoesNotThrow();
            AHandlerThatRemovesItselfStillGetsOneMoreCall();
        }

        // ---- Regression: issue #16 ---------------------------------------------------------------
        //
        // A mod may register a Mod Menu entry from any thread, because a plugin that registers from
        // the callback of a web request or a file read is doing something ordinary. OnGUI walks the
        // entries every frame and more than once per frame. When both touched one List, the walk
        // threw "Collection was modified"; ModApiPlugin.OnGUI caught it and closed the menu, so one
        // mod's registration timing shut the shared hub on every other mod.

        private static void RegisteringWhileDrawingDoesNotThrow()
        {
            Exception drawFailure = null;
            bool stop = false;
            int frames = 0;

            // The draw side: walk the snapshot the way DrawHub does, as fast as it can.
            var drawing = new Thread(delegate ()
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        ModMenuEntry[] entries = ModMenuRegistry.Snapshot;
                        for (int i = 0; i < entries.Length; i++)
                        {
                            // Touching a member is what a real frame does, and it is what turns a
                            // torn snapshot into a NullReferenceException.
                            if (entries[i].Title == null) throw new InvalidOperationException("null title");
                        }
                        frames++;
                    }
                }
                catch (Exception e) { drawFailure = e; }
            });

            drawing.IsBackground = true;
            drawing.Start();

            // The registration side: churn entries from this thread while the other one walks.
            DateTime until = DateTime.UtcNow + Duration;
            int churns = 0;
            while (DateTime.UtcNow < until)
            {
                string title = "issue16-" + (churns % 8);
                ModMenuRegistry.Register(title, "regression", delegate { });
                ModMenuRegistry.Unregister("issue16-" + ((churns + 4) % 8));
                churns++;
            }

            Volatile.Write(ref stop, true);
            drawing.Join(5000);

            for (int i = 0; i < 8; i++) ModMenuRegistry.Unregister("issue16-" + i);

            True("issue #16: the draw side actually ran (" + frames + " walks against " + churns + " changes)",
                 frames > 0 && churns > 0);

            if (drawFailure == null) Pass("issue #16: registering from another thread never broke a walk");
            else Fail("issue #16: walking the entries threw while another thread registered -- "
                      + drawFailure.GetType().Name + ": " + drawFailure.Message);
        }

        /// <summary>
        /// The snapshot is replaced whole and never modified in place, so any array a reader holds
        /// stays valid and self-consistent however many changes land afterwards.
        /// </summary>
        private static void SnapshotIsNeverSeenHalfWritten()
        {
            ModMenuRegistry.Register("issue16-held-a", "one", delegate { });
            ModMenuRegistry.Register("issue16-held-b", "two", delegate { });

            ModMenuEntry[] held = ModMenuRegistry.Snapshot;
            int lengthWhenTaken = held.Length;

            for (int i = 0; i < 50; i++)
            {
                ModMenuRegistry.Register("issue16-churn-" + i, "churn", delegate { });
                ModMenuRegistry.Unregister("issue16-churn-" + i);
            }

            True("issue #16: an array taken earlier is not resized under the reader",
                 held.Length == lengthWhenTaken);

            bool intact = true;
            foreach (ModMenuEntry e in held)
                if (e == null || e.Title == null) intact = false;

            True("issue #16: and every entry in it is still whole", intact);
            True("issue #16: while the live snapshot moved on", ModMenuRegistry.Snapshot != held);

            ModMenuRegistry.Unregister("issue16-held-a");
            ModMenuRegistry.Unregister("issue16-held-b");
        }

        // ---- Regression: issue #26 ---------------------------------------------------------------
        //
        // A multicast event field goes null on the last -=, so a Raise method that reads the field
        // twice -- once to null-check, once for GetInvocationList -- lets that -= land between the
        // two reads and throws NullReferenceException. Safe does not cover it: Safe wraps the
        // subscriber call, and this throws while building the list of subscribers to call. What is
        // left is an uncaught exception in a Harmony postfix on the game thread.

        private static void UnsubscribingWhileRaisingDoesNotThrow()
        {
            Exception raiseFailure = null;
            bool stop = false;
            int raises = 0;

            Action<bool> handler = delegate { };

            // The subscriber side: the last -= is the one that nulls the field, so this spends most
            // of its time crossing exactly the boundary the bug needed.
            var churning = new Thread(delegate ()
            {
                while (!Volatile.Read(ref stop))
                {
                    AS2Events.SettingsDialogToggled += handler;
                    AS2Events.SettingsDialogToggled -= handler;
                }
            });

            churning.IsBackground = true;
            churning.Start();

            try
            {
                DateTime until = DateTime.UtcNow + Duration;
                while (DateTime.UtcNow < until)
                {
                    AS2Events.RaiseSettingsDialog(true);
                    raises++;
                }
            }
            catch (Exception e) { raiseFailure = e; }
            finally
            {
                Volatile.Write(ref stop, true);
                churning.Join(5000);
            }

            True("issue #26: the raise side actually ran (" + raises + " raises)", raises > 0);

            if (raiseFailure == null)
                Pass("issue #26: raising never threw while a subscriber came and went");
            else
                Fail("issue #26: raising threw while a subscriber came and went -- "
                     + raiseFailure.GetType().Name + ": " + raiseFailure.Message);
        }

        /// <summary>
        /// The documented consequence of the fix, and the reason it is documented: a raise uses the
        /// handler list as it stood when it started, so a handler that unsubscribes itself can still
        /// be called once more. A mod is told to tolerate that, so it has to stay true.
        /// </summary>
        private static void AHandlerThatRemovesItselfStillGetsOneMoreCall()
        {
            int calls = 0;
            Action<bool> first = null;
            Action<bool> second = delegate { calls++; };

            first = delegate
            {
                calls++;
                AS2Events.SettingsDialogToggled -= second;
            };

            AS2Events.SettingsDialogToggled += first;
            AS2Events.SettingsDialogToggled += second;

            AS2Events.RaiseSettingsDialog(true);

            True("issue #26: a handler removed mid-raise is still called for that raise", calls == 2);

            calls = 0;
            AS2Events.RaiseSettingsDialog(false);
            True("issue #26: and is gone from the next one", calls == 1);

            AS2Events.SettingsDialogToggled -= first;
        }
    }
}
