using System;

namespace AS2.ModApi
{
    /// <summary>The closed set of game broadcasts this framework carries, and each call shape</summary>
    // Three facts, and each one turns a plausible-looking edit into a bug in somebody else's code
    //
    // 1. Arity is not a style choice. Messenger, Messenger<T>, Messenger<T,U> and Messenger<T,U,V>
    //    all alias one shared Dictionary<string,Delegate> from MessengerInternal.eventTable, so a
    //    name has exactly one legal delegate type across the process
    //    A wrong one throws ListenerException on add, and the game's next Broadcast throws too
    //    Every shape below was read off the IL of the shipped Assembly-CSharp, not guessed
    //
    // 2. The default broadcast mode is REQUIRE_LISTENER, so a Broadcast with no listener throws
    //    BroadcastException. Adding a listener can only prevent a throw, and removing the last one
    //    can cause it, which is why MessengerBridge never unsubscribes
    //
    // 3. "GameplayEnd" is a dead name and is deliberately absent. 32 sites call AddListener or
    //    RemoveListener on it and no site broadcasts it. Use EndCleanup, which fires on a natural
    //    song end, on End Song from the pause menu, and on Restart
    //
    // Stays free of Unity, BepInEx and the game's assemblies, because it compiles into
    // tests/AS2.ModApi.Tests. That is also why parameter types are strings rather than Type
    internal static class MessageTable
    {
        // ---- Names -------------------------------------------------------------------------------
        //
        // internal, so a mod cannot reach them. The framework supports the messages named here and
        // nothing else: there is no public API anywhere that takes a message name, so a mod cannot
        // ask the bridge for a broadcast this table does not already declare. A mod is of course
        // free to call Messenger.AddListener itself -- the game's class is public and we cannot
        // change that -- but then it owns the arity problem in fact 1 above.

        internal const string GameplayStart = "GameplayStart";
        internal const string EndCleanup = "EndCleanup";
        internal const string SongStartedPlaying = "SongStartedPlaying";
        internal const string SongEnded = "SongEnded";
        internal const string PausingGame = "PausingGame";
        internal const string ResumingGame = "ResumingGame";
        internal const string PlayerLanded = "PlayerLanded";
        internal const string PlayerCrashLanded = "PlayerCrashLanded";
        internal const string SkinChanged = "SkinChanged";
        internal const string ModeChanged = "ModeChanged";
        internal const string BrowseSongsClicked = "BrowseSongsClicked";
        internal const string ShuttingDown = "ShuttingDown";

        internal const string SendingRideScore = "SendingRideScore";
        internal const string TrickStarted = "TrickStarted";
        internal const string PlayerJumped = "PlayerJumped";

        /// <summary>The running score, as a total</summary>
        // Not <see cref="ScoreChanged"/>, which is the trap: that one is broadcast by Flyups and
        // TrickHUD and listened to by ScoreManager, so it is an input to scoring, not its output
        // OnTrafficCollected and OnAddPoints add points without passing through it, so adding
        // ScoreChanged up yields a fraction of the real score
        // ScoreManager.AddPoints computes the new total and broadcasts it here
        internal const string ScoreUpdated = "ScoreUpdated";
        internal const string SongChanged = "SongChanged";
        internal const string AboutToChangeSong = "AboutToChangeSong";
        internal const string LuaSkinError = "LuaSkinError";
        internal const string LuaModError = "LuaModError";
        internal const string LuaSkinPrintText = "LuaSkinPrintText";

        internal const string ScoreChanged = "ScoreChanged";
        internal const string TrickDone = "TrickDone";
        internal const string PlayerLandedWithPoints = "PlayerLandedWithPoints";

        internal const string TrafficCollected = "TrafficCollected";
        internal const string TrafficMissed = "TrafficMissed";

        // ---- Signatures --------------------------------------------------------------------------

        /// <summary>Canonical spellings for <see cref="Entry.Signature"/>. Zero arguments is ""</summary>
        internal const string None = "";
        internal const string Int = "int";
        internal const string Float = "float";
        internal const string String = "string";
        internal const string Song = "Song";
        internal const string FloatInt = "float,int";
        internal const string IntInt = "int,int";
        internal const string IntIntVector3 = "int,int,Vector3";

        /// <summary>One declared message: its name and the exact Messenger call shape</summary>
        internal sealed class Entry
        {
            internal readonly string Name;
            internal readonly string Signature;

            internal Entry(string name, string signature)
            {
                Name = name;
                Signature = signature;
            }

            /// <summary>Number of broadcast arguments, derived from the signature</summary>
            internal int Arity
            {
                get
                {
                    if (Signature.Length == 0) return 0;
                    int commas = 0;
                    for (int i = 0; i < Signature.Length; i++)
                        if (Signature[i] == ',') commas++;
                    return commas + 1;
                }
            }
        }

        /// <summary>
        /// The whole supported set. Adding a row here is only half the work: the message also needs
        /// a wire method in <see cref="MessengerBridge"/> and an event on AS2GameEvents, and both of
        /// those are hand-written on purpose. There is no loop that turns this array into
        /// subscriptions, because a loop would need a message name at run time and that is exactly
        /// the door this table exists to keep shut.
        /// </summary>
        internal static readonly Entry[] All =
        {
            new Entry(GameplayStart, None),
            new Entry(EndCleanup, None),
            new Entry(SongStartedPlaying, None),
            new Entry(SongEnded, None),
            new Entry(PausingGame, None),
            new Entry(ResumingGame, None),
            new Entry(PlayerLanded, None),
            new Entry(PlayerCrashLanded, None),
            new Entry(SkinChanged, None),
            new Entry(ModeChanged, None),
            new Entry(BrowseSongsClicked, None),
            new Entry(ShuttingDown, None),

            new Entry(SendingRideScore, Int),
            new Entry(TrickStarted, Int),
            new Entry(PlayerJumped, Float),
            new Entry(ScoreUpdated, Float),
            new Entry(SongChanged, Song),
            new Entry(AboutToChangeSong, Song),
            new Entry(LuaSkinError, String),
            new Entry(LuaModError, String),
            new Entry(LuaSkinPrintText, String),

            new Entry(ScoreChanged, FloatInt),
            new Entry(TrickDone, IntInt),
            new Entry(PlayerLandedWithPoints, IntInt),

            new Entry(TrafficCollected, IntIntVector3),
            new Entry(TrafficMissed, IntIntVector3),
        };

        /// <summary>The entry for a name, or null when the name is not in the supported set</summary>
        internal static Entry Find(string name)
        {
            if (name == null) return null;
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Name, name, StringComparison.Ordinal)) return All[i];
            return null;
        }

        /// <summary>True when the name is declared with exactly this argument count</summary>
        // MessengerBridge asks before every AddListener, which stops a typo in a wire method
        // reaching the game's shared event table
        // A table row nobody wires is harmless. A wire method that does not match its row is the
        // bug worth catching, and it is caught here at run time and in the cold checks
        internal static bool Declares(string name, int arity)
        {
            Entry e = Find(name);
            return e != null && e.Arity == arity;
        }
    }
}
