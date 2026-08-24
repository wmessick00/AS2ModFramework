using System;
using System.Collections.Generic;
using UnityEngine;

namespace AS2.ModApi
{
    /// <summary>The one place in this framework that talks to the game's own event bus</summary>
    // The game's Messenger is public and carries about 194 message names, so a mod could subscribe
    // to any of them itself. This class keeps the framework's supported surface a closed set:
    // MessageTable declares every name and shape, this is internal, and no public API takes a name
    //
    // Nothing here broadcasts. There is no Broadcast call in this assembly, and
    // tools/verify-invariants.ps1 fails the release build if one appears
    //
    // Subscribe once, never unsubscribe. This Messenger copy has no Cleanup and no
    // MarkAsPermanent, and nothing outside the four Messenger classes reads the event table, so a
    // listener lives for the process
    // Removing is worse than useless: OnListenerRemoved drops the key when the last listener
    // leaves, and the default mode is REQUIRE_LISTENER, so a later broadcast throws inside the game
    //
    // Every handler catches everything. Messenger.Broadcast has no exception handler in any of its
    // 8 overloads, so an escaped exception abandons the game's method part-way through
    // Let one out of the SendingRideScore handler and Game.OnSongEnded stops before it saves
    // settings and returns to the Hub
    //
    // Wire from the main thread. AddListener writes a plain Dictionary with no lock of its own
    // The lock here stops two mods' first += racing each other, and cannot help against the game
    // thread broadcasting at the same moment
    internal static class MessengerBridge
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Wired = new HashSet<string>(StringComparer.Ordinal);

        // ---- Wiring, one method per message ----------------------------------------------------
        //
        // Written out by hand rather than generated from MessageTable.All in a loop. A loop would
        // need to turn a string into a delegate type at run time, which is precisely the door this
        // design closes; and each pair below is a place a reader can check the name against the
        // arity without holding a table in their head. The cost is that a new message means editing
        // three files, which is the intended amount of friction.

        internal static void EnsureGameplayStart() { Wire0(MessageTable.GameplayStart, OnGameplayStart); }
        internal static void EnsureEndCleanup() { Wire0(MessageTable.EndCleanup, OnEndCleanup); }
        internal static void EnsureSongStartedPlaying() { Wire0(MessageTable.SongStartedPlaying, OnSongStartedPlaying); }
        internal static void EnsureSongEnded() { Wire0(MessageTable.SongEnded, OnSongEnded); }
        internal static void EnsurePausingGame() { Wire0(MessageTable.PausingGame, OnPausingGame); }
        internal static void EnsureResumingGame() { Wire0(MessageTable.ResumingGame, OnResumingGame); }
        internal static void EnsurePlayerLanded() { Wire0(MessageTable.PlayerLanded, OnPlayerLanded); }
        internal static void EnsurePlayerCrashLanded() { Wire0(MessageTable.PlayerCrashLanded, OnPlayerCrashLanded); }
        internal static void EnsureSkinChanged() { Wire0(MessageTable.SkinChanged, OnSkinChanged); }
        internal static void EnsureModeChanged() { Wire0(MessageTable.ModeChanged, OnModeChanged); }
        internal static void EnsureBrowseSongsClicked() { Wire0(MessageTable.BrowseSongsClicked, OnBrowseSongsClicked); }
        internal static void EnsureShuttingDown() { Wire0(MessageTable.ShuttingDown, OnShuttingDown); }

        internal static void EnsureSendingRideScore() { Wire1<int>(MessageTable.SendingRideScore, OnSendingRideScore); }
        internal static void EnsureTrickStarted() { Wire1<int>(MessageTable.TrickStarted, OnTrickStarted); }
        internal static void EnsurePlayerJumped() { Wire1<float>(MessageTable.PlayerJumped, OnPlayerJumped); }
        internal static void EnsureScoreUpdated() { Wire1<float>(MessageTable.ScoreUpdated, OnScoreUpdated); }
        internal static void EnsureSongChanged() { Wire1<Song>(MessageTable.SongChanged, OnSongChanged); }
        internal static void EnsureAboutToChangeSong() { Wire1<Song>(MessageTable.AboutToChangeSong, OnAboutToChangeSong); }
        internal static void EnsureLuaSkinError() { Wire1<string>(MessageTable.LuaSkinError, OnLuaSkinError); }
        internal static void EnsureLuaModError() { Wire1<string>(MessageTable.LuaModError, OnLuaModError); }
        internal static void EnsureLuaSkinPrintText() { Wire1<string>(MessageTable.LuaSkinPrintText, OnLuaSkinPrintText); }

        internal static void EnsureScoreChanged() { Wire2<float, int>(MessageTable.ScoreChanged, OnScoreChanged); }
        internal static void EnsureTrickDone() { Wire2<int, int>(MessageTable.TrickDone, OnTrickDone); }
        internal static void EnsurePlayerLandedWithPoints() { Wire2<int, int>(MessageTable.PlayerLandedWithPoints, OnPlayerLandedWithPoints); }

        internal static void EnsureTrafficCollected() { Wire3<int, int, Vector3>(MessageTable.TrafficCollected, OnTrafficCollected); }
        internal static void EnsureTrafficMissed() { Wire3<int, int, Vector3>(MessageTable.TrafficMissed, OnTrafficMissed); }

        // ---- Handlers --------------------------------------------------------------------------
        //
        // Each one hands straight to AS2GameEvents through Guard, which is the try/catch boundary
        // the game does not provide.

        private static void OnGameplayStart() { Guard(MessageTable.GameplayStart, AS2GameEvents.RaiseGameplayStarted); }
        private static void OnEndCleanup() { Guard(MessageTable.EndCleanup, AS2GameEvents.RaiseRideEnded); }
        private static void OnSongStartedPlaying() { Guard(MessageTable.SongStartedPlaying, AS2GameEvents.RaiseSongStarted); }
        private static void OnSongEnded() { Guard(MessageTable.SongEnded, AS2GameEvents.RaiseSongEnded); }
        private static void OnPausingGame() { Guard(MessageTable.PausingGame, AS2GameEvents.RaisePaused); }
        private static void OnResumingGame() { Guard(MessageTable.ResumingGame, AS2GameEvents.RaiseResumed); }
        private static void OnPlayerLanded() { Guard(MessageTable.PlayerLanded, AS2GameEvents.RaisePlayerLanded); }
        private static void OnPlayerCrashLanded() { Guard(MessageTable.PlayerCrashLanded, AS2GameEvents.RaisePlayerCrashLanded); }
        private static void OnSkinChanged() { Guard(MessageTable.SkinChanged, AS2GameEvents.RaiseSkinChanged); }
        private static void OnModeChanged() { Guard(MessageTable.ModeChanged, AS2GameEvents.RaiseModeChanged); }
        private static void OnBrowseSongsClicked() { Guard(MessageTable.BrowseSongsClicked, AS2GameEvents.RaiseMusicBrowserOpened); }
        private static void OnShuttingDown() { Guard(MessageTable.ShuttingDown, AS2GameEvents.RaiseGameShuttingDown); }

        /// <summary>
        /// The int argument is the final score, and it is the only moment it is trustworthy --
        /// Game.OnSongEnded finalises the scorecard between the RequestFinalScoring broadcast and
        /// this one. See the timing note on <see cref="RideResult"/>.
        /// </summary>
        private static void OnSendingRideScore(int score)
        {
            Guard(MessageTable.SendingRideScore,
                  delegate { AS2GameEvents.RaiseRideScored(ScorecardReader.Read(score)); });
        }

        private static void OnTrickStarted(int trickId)
        {
            Guard(MessageTable.TrickStarted, delegate { AS2GameEvents.RaiseTrickStarted(trickId); });
        }

        private static void OnPlayerJumped(float height)
        {
            Guard(MessageTable.PlayerJumped, delegate { AS2GameEvents.RaisePlayerJumped(height); });
        }

        /// <summary>The new running total, straight out of ScoreManager.AddPoints</summary>
        private static void OnScoreUpdated(float total)
        {
            Guard(MessageTable.ScoreUpdated, delegate { AS2GameEvents.RaiseScoreUpdated(total); });
        }

        private static void OnSongChanged(Song song)
        {
            Guard(MessageTable.SongChanged,
                  delegate { AS2GameEvents.RaiseSongChanged(ScorecardReader.Describe(song)); });
        }

        private static void OnAboutToChangeSong(Song song)
        {
            Guard(MessageTable.AboutToChangeSong,
                  delegate { AS2GameEvents.RaiseSongAboutToChange(ScorecardReader.Describe(song)); });
        }

        private static void OnLuaSkinError(string message)
        {
            Guard(MessageTable.LuaSkinError, delegate { AS2GameEvents.RaiseLuaSkinError(message); });
        }

        private static void OnLuaModError(string message)
        {
            Guard(MessageTable.LuaModError, delegate { AS2GameEvents.RaiseLuaModError(message); });
        }

        private static void OnLuaSkinPrintText(string message)
        {
            Guard(MessageTable.LuaSkinPrintText, delegate { AS2GameEvents.RaiseLuaScriptPrint(message); });
        }

        private static void OnScoreChanged(float delta, int playerNumber)
        {
            Guard(MessageTable.ScoreChanged,
                  delegate { AS2GameEvents.RaiseScoreChanged(delta, playerNumber); });
        }

        private static void OnTrickDone(int trickId, int trickPoints)
        {
            Guard(MessageTable.TrickDone, delegate { AS2GameEvents.RaiseTrickDone(trickId, trickPoints); });
        }

        private static void OnPlayerLandedWithPoints(int points, int jumpNumber)
        {
            Guard(MessageTable.PlayerLandedWithPoints,
                  delegate { AS2GameEvents.RaisePlayerLandedWithPoints(points, jumpNumber); });
        }

        private static void OnTrafficCollected(int blockType, int lane, Vector3 position)
        {
            Guard(MessageTable.TrafficCollected,
                  delegate { AS2GameEvents.RaiseTrafficCollected(blockType, lane, position); });
        }

        private static void OnTrafficMissed(int blockType, int lane, Vector3 position)
        {
            Guard(MessageTable.TrafficMissed,
                  delegate { AS2GameEvents.RaiseTrafficMissed(blockType, lane, position); });
        }

        // ---- Plumbing --------------------------------------------------------------------------

        private static void Wire0(string name, Callback handler)
        {
            if (!Claim(name, 0)) return;
            try { Messenger.AddListener(name, handler); }
            catch (Exception e) { Failed(name, e); }
        }

        private static void Wire1<T>(string name, Callback<T> handler)
        {
            if (!Claim(name, 1)) return;
            try { Messenger<T>.AddListener(name, handler); }
            catch (Exception e) { Failed(name, e); }
        }

        private static void Wire2<T, U>(string name, Callback<T, U> handler)
        {
            if (!Claim(name, 2)) return;
            try { Messenger<T, U>.AddListener(name, handler); }
            catch (Exception e) { Failed(name, e); }
        }

        private static void Wire3<T, U, V>(string name, Callback<T, U, V> handler)
        {
            if (!Claim(name, 3)) return;
            try { Messenger<T, U, V>.AddListener(name, handler); }
            catch (Exception e) { Failed(name, e); }
        }

        /// <summary>Decides whether this call is the one that subscribes</summary>
        // False means the name is already wired, which is the common case since every += after the
        // first lands here, or that the wire method disagrees with MessageTable
        // The refusal is the important half. All four Messenger classes share one event table, so a
        // wrong argument count makes the game's own AddListener calls throw ListenerException
        // Better to lose one framework event and say so than to break a listener the game owns
        private static bool Claim(string name, int arity)
        {
            if (!MessageTable.Declares(name, arity))
            {
                ModApiPlugin.Log.LogError(
                    "Refusing to subscribe to '" + name + "' with " + arity + " argument(s): the "
                    + "message table does not declare that shape. This is a bug in AS2.ModApi.");
                return false;
            }

            lock (Gate)
            {
                if (Wired.Contains(name)) return false;
                Wired.Add(name);
                return true;
            }
        }

        /// <summary>
        /// A failed AddListener stays claimed on purpose. It failed for a reason that will not
        /// change -- most likely the game renamed the message, or another mod got there first with a
        /// different signature -- and retrying on every `+=` would only repeat the warning.
        /// </summary>
        private static void Failed(string name, Exception e)
        {
            ModApiPlugin.Log.LogWarning(
                "Could not subscribe to the game's '" + name + "' message; that event will not "
                + "fire. " + e.Message);
        }

        /// <summary>
        /// The boundary the game does not provide. Broadcast has no exception handler, so anything
        /// that escapes here abandons the game's own method half-done.
        /// </summary>
        private static void Guard(string name, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogError("The '" + name + "' bridge threw: " + e);
            }
        }
    }
}
