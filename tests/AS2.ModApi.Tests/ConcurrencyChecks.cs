using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using static AS2.Tests.Check;

namespace AS2.ModApi.Tests
{
    /// <summary>The racing checks: two bugs this framework fixed, and two files shaped like them</summary>
    // #16 and #26 shipped first. Neither had a check, which is the worst shape for a bug of this kind:
    //   -the fix is a few careful lines that look like style
    //   -a later reader has every reason to simplify them
    //   -the failure only appears on somebody else's machine, at a timing they cannot reproduce
    // TargetResolver's guards have had regression checks for #15, #2 and #13 for the same reason
    // MessengerBridge and AS2GameEvents have never shipped one and are covered here anyway, per #46
    // They hold hand-rolled locks over the same three moves -- claim a name once, add to a multicast
    // field, walk a snapshot of it -- so the next bug of this kind is likelier to be in one of them
    // than anywhere else in the framework
    // A racing check cannot prove the absence of a race. It can drive the threads hard enough that
    // the unfixed code fails almost every run, which is the honest bargain
    // Every check here was run against the broken shape first and seen to go red: take the lock out
    // of ModMenuRegistry or of AS2GameEvents' add and remove, drop the local copy in AS2Events.Raise
    // or in AS2GameEvents.Raise, take the lock out of MessengerBridge.Claim
    // Run them the way the wiki and cold-checks.yml do, with a plain dotnet run. Under -c Release the
    // JIT keeps a static delegate field in a register across the null check and the walk that follows
    // it, so neither double-read shape can fail there and both checks pass whatever the source says
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

            WiringAMessageHappensOnceHoweverManyThreadsAsk();
            AFailedSubscriptionStaysClaimed();
            NoSubscriptionIsLostWhenModsSubscribeAtOnce();
            RaisingAGameEventWhileSubscribersChurnDoesNotThrow();
            AGameHandlerThatRemovesItselfStillGetsOneMoreCall();
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

        // ---- Cover without a bug behind it: MessengerBridge ---------------------------------------
        //
        // #46. MessengerBridge.Claim is the decision about whether this call is the one that
        // subscribes, and the cost of a wrong answer is paid in the game rather than here: a second
        // AddListener for the same name makes the game's next broadcast run the framework's handler
        // twice, so every mod on that event sees the ride score twice
        // Mods subscribe from Awake, which is one thread, right up until one of them subscribes from
        // a web callback -- and the whole reason Claim holds a lock is that nothing stops that

        private static void WiringAMessageHappensOnceHoweverManyThreadsAsk()
        {
            if (!TryResetWiring())
            {
                Fail("MessengerBridge: the claimed-name set could not be reached, so the wiring "
                     + "race never ran. If the field was renamed, rename it in TryResetWiring too.");
                return;
            }

            const int racers = 8;
            int rounds = 0;
            int roundsWiredTwice = 0;
            Exception wireFailure = null;
            object sync = new object();

            DateTime until = DateTime.UtcNow + Duration;
            while (DateTime.UtcNow < until)
            {
                // Every round starts from nothing wired, so every round races the first subscribe
                // rather than the cheap "already claimed" path every one after it takes.
                TryResetWiring();

                RunTogether(racers, delegate
                {
                    try { MessengerBridge.EnsureGameplayStart(); }
                    catch (Exception e) { lock (sync) wireFailure = e; }
                });

                if (MessengerInternal.AddsFor(MessageTable.GameplayStart) != 1) roundsWiredTwice++;
                rounds++;
            }

            True("MessengerBridge: the wiring race actually ran (" + racers + " threads over "
                 + rounds + " rounds)", rounds > 0);

            if (wireFailure == null)
                Pass("MessengerBridge: claiming a name never threw under contention");
            else
                Fail("MessengerBridge: claiming a name threw under contention -- "
                     + wireFailure.GetType().Name + ": " + wireFailure.Message);

            True("MessengerBridge: exactly one thread per round reached the game's AddListener ("
                 + roundsWiredTwice + " rounds did not)", roundsWiredTwice == 0);

            List<string> claimed = MessengerInternal.Claimed();
            True("MessengerBridge: and wiring one message claimed no other name",
                 claimed.Count == 1 && claimed[0] == MessageTable.GameplayStart);
        }

        /// <summary>
        /// The other half of what the claimed-name set is for. A message that failed to subscribe
        /// stays claimed, so the framework asks the game once and never again.
        /// </summary>
        // Documented in MessengerBridge.Failed and worth pinning: AddListener fails for reasons that
        // do not change between calls, and a mod's subscribe runs once per mod
        // Retrying would put one warning in the log per subscriber, for an event that cannot fire
        private static void AFailedSubscriptionStaysClaimed()
        {
            if (!TryResetWiring())
            {
                Fail("MessengerBridge: the claimed-name set could not be reached, so the failed "
                     + "subscription check never ran.");
                return;
            }

            ModApiPlugin.Log.Clear();
            MessengerInternal.ThrowOn = MessageTable.SongEnded;
            MessengerBridge.EnsureSongEnded();
            MessengerInternal.ThrowOn = null;

            True("MessengerBridge: a subscription the game refused is reported",
                 ModApiPlugin.Log.Mentions("Could not subscribe"));

            ModApiPlugin.Log.Clear();
            MessengerBridge.EnsureSongEnded();

            False("MessengerBridge: and the next subscriber does not repeat the warning",
                  ModApiPlugin.Log.Mentions("Could not subscribe"));
            True("MessengerBridge: nor does it ask the game a second time",
                 MessengerInternal.AddsFor(MessageTable.SongEnded) == 0);
        }

        // ---- Cover without a bug behind it: AS2GameEvents -----------------------------------------
        //
        // #46. The same three moves as AS2Events above -- add to a multicast field, remove from it,
        // walk a snapshot of it -- across 26 events instead of 4, behind the same kind of Gate
        // A compound assignment on a delegate field is a read, a combine and a write, so two mods
        // subscribing at the same moment without the lock leave one of them attached to a delegate
        // the other has already replaced
        // Nothing throws and nothing logs. That mod's handler simply never runs again

        private static void NoSubscriptionIsLostWhenModsSubscribeAtOnce()
        {
            const int racers = 8;
            const int each = 250;

            int calls = 0;
            var handlers = new Action<float>[racers][];

            for (int r = 0; r < racers; r++)
            {
                handlers[r] = new Action<float>[each];
                for (int h = 0; h < each; h++)
                    handlers[r][h] = delegate { Interlocked.Increment(ref calls); };
            }

            RunTogether(racers, delegate (int me)
            {
                for (int h = 0; h < each; h++) AS2GameEvents.ScoreUpdated += handlers[me][h];
            });

            calls = 0;
            AS2GameEvents.RaiseScoreUpdated(1f);
            True("AS2GameEvents: no subscribe is lost when " + racers + " threads arrive at once ("
                 + calls + " of " + (racers * each) + " ran)", calls == racers * each);

            RunTogether(racers, delegate (int me)
            {
                for (int h = 0; h < each; h++) AS2GameEvents.ScoreUpdated -= handlers[me][h];
            });

            calls = 0;
            AS2GameEvents.RaiseScoreUpdated(2f);
            True("AS2GameEvents: and none survives the unsubscribe that removed it ("
                 + calls + " left)", calls == 0);
        }

        /// <summary>The #26 shape, on the file with 26 chances to get it wrong instead of 4</summary>
        // A payload event on purpose: the generic raisers build a closure per subscriber, so they
        // hold the snapshot across more work than the no-argument one does
        private static void RaisingAGameEventWhileSubscribersChurnDoesNotThrow()
        {
            Exception raiseFailure = null;
            bool stop = false;
            int raises = 0;

            Action<int, int, Vector3> handler = delegate { };

            var churning = new Thread(delegate ()
            {
                while (!Volatile.Read(ref stop))
                {
                    AS2GameEvents.TrafficCollected += handler;
                    AS2GameEvents.TrafficCollected -= handler;
                }
            });

            churning.IsBackground = true;
            churning.Start();

            try
            {
                var somewhere = new Vector3(1f, 2f, 3f);
                DateTime until = DateTime.UtcNow + Duration;
                while (DateTime.UtcNow < until)
                {
                    AS2GameEvents.RaiseTrafficCollected(4, 1, somewhere);
                    raises++;
                }
            }
            catch (Exception e) { raiseFailure = e; }
            finally
            {
                Volatile.Write(ref stop, true);
                churning.Join(5000);
            }

            True("AS2GameEvents: the raise side actually ran (" + raises + " raises)", raises > 0);

            if (raiseFailure == null)
                Pass("AS2GameEvents: raising never threw while a subscriber came and went");
            else
                Fail("AS2GameEvents: raising threw while a subscriber came and went -- "
                     + raiseFailure.GetType().Name + ": " + raiseFailure.Message);
        }

        /// <summary>
        /// The same documented consequence the AS2Events check pins, restated on this file because
        /// its raisers make the promise separately and a mod reads one wiki page or the other.
        /// </summary>
        private static void AGameHandlerThatRemovesItselfStillGetsOneMoreCall()
        {
            int calls = 0;
            Action first = null;
            Action second = delegate { calls++; };

            first = delegate
            {
                calls++;
                AS2GameEvents.SkinChanged -= second;
            };

            AS2GameEvents.SkinChanged += first;
            AS2GameEvents.SkinChanged += second;

            AS2GameEvents.RaiseSkinChanged();

            True("AS2GameEvents: a subscriber removed mid-raise is still called for that raise",
                 calls == 2);

            calls = 0;
            AS2GameEvents.RaiseSkinChanged();
            True("AS2GameEvents: and is gone from the next one", calls == 1);

            AS2GameEvents.SkinChanged -= first;
        }

        // ---- Plumbing ------------------------------------------------------------------------------

        /// <summary>
        /// Runs the body on the given number of threads that all start at the same moment, and
        /// returns once every one of them has finished. The body is handed its own index.
        /// </summary>
        // The gate is the point, and it spins rather than waiting on an event. Threads started in a
        // loop reach the code under test milliseconds apart, which is wide enough for an unlocked
        // read-modify-write to look correct
        // A ManualResetEvent is better and still costs one kernel wake per thread, one after
        // another: against an unlocked Claim it caught 1 round in 750. Threads already spinning on
        // a flag enter within tens of nanoseconds of it turning, and catch 5 to 9 rounds in 500
        // The last thread to arrive is the one that lets go, so nobody spins for long
        private static void RunTogether(int racers, Action<int> body)
        {
            int waiting = 0;
            int go = 0;
            var threads = new Thread[racers];

            for (int i = 0; i < racers; i++)
            {
                int me = i;
                threads[i] = new Thread(delegate ()
                {
                    Interlocked.Increment(ref waiting);
                    while (Volatile.Read(ref go) == 0) Thread.SpinWait(1);
                    body(me);
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }

            while (Volatile.Read(ref waiting) < racers) Thread.Sleep(0);
            Volatile.Write(ref go, 1);

            for (int i = 0; i < racers; i++) threads[i].Join(30000);
        }

        /// <summary>Empties MessengerBridge's claimed-name set, and the shim bus behind it</summary>
        // The bridge has no reset and should not grow one. The game never needs a message unwired --
        // removing the last listener is what makes its own next broadcast throw -- so a production
        // method whose only caller is a check would be a liability with no other use
        // So this reaches the private field, and the caller fails rather than skips when it is not
        // there. A rename that quietly turned the racing check into a single round would be worse
        // than a red build
        private static bool TryResetWiring()
        {
            FieldInfo field = typeof(MessengerBridge).GetField(
                "Wired", BindingFlags.NonPublic | BindingFlags.Static);

            var wired = field == null ? null : field.GetValue(null) as HashSet<string>;
            if (wired == null) return false;

            wired.Clear();
            MessengerInternal.Reset();
            return true;
        }
    }
}
