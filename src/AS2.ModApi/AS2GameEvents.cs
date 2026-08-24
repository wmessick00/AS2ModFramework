using System;
using UnityEngine;

namespace AS2.ModApi
{
    /// <summary>What the game is doing, as events</summary>
    // No Harmony patch behind any of these. The game broadcasts them already, so this only listens
    // Read-only, and enforced rather than promised: no payload is a game object, only SongInfo and
    // RideResult copies, and tools/verify-invariants.ps1 checks the built DLL calls no game setter
    // The set is closed. There is no overload taking a message name, so adding one means editing
    // MessageTable, MessengerBridge and this file
    // Subscribe from Awake on the main thread. The first += is what attaches the framework to the
    // broadcast, and the game's AddListener writes an unguarded Dictionary
    // Unsubscribing never detaches the framework from the game -- see MessengerBridge for why
    // Subscribers are isolated: one handler that throws is logged, and the others still run
    // The 26 events and what each one means: see the AS2GameEvents wiki page

    public static class AS2GameEvents
    {
        private static readonly object Gate = new object();

        // ---- Ride lifecycle --------------------------------------------------------------------

        private static Action _gameplayStarted;
        /// <summary>A ride began. The highway is built and the player is about to surf</summary>
        public static event Action GameplayStarted
        {
            add { MessengerBridge.EnsureGameplayStart(); lock (Gate) _gameplayStarted += value; }
            remove { lock (Gate) _gameplayStarted -= value; }
        }

        private static Action _rideEnded;
        /// <summary>A ride finished, by any route</summary>
        // The song ended, or the player chose End Song or Restart. Prefer this over SongEnded for
        // "the ride is over" rather than "the audio ran out"
        // Carries no score on purpose: it maps to EndCleanup, which fires after the scorecard is
        // finalised but before the score is sent. Use RideScored for the number
        public static event Action RideEnded
        {
            add { MessengerBridge.EnsureEndCleanup(); lock (Gate) _rideEnded += value; }
            remove { lock (Gate) _rideEnded -= value; }
        }

        private static Action<RideResult> _rideScored;
        /// <summary>A ride was scored</summary>
        // The one moment the whole scorecard is valid, so the moment to record a ride
        // Does not fire when the player quits a song early
        public static event Action<RideResult> RideScored
        {
            add { MessengerBridge.EnsureSendingRideScore(); lock (Gate) _rideScored += value; }
            remove { lock (Gate) _rideScored -= value; }
        }

        private static Action _paused;
        /// <summary>The pause menu came up</summary>
        public static event Action Paused
        {
            add { MessengerBridge.EnsurePausingGame(); lock (Gate) _paused += value; }
            remove { lock (Gate) _paused -= value; }
        }

        private static Action _resumed;
        /// <summary>The player left the pause menu and the ride continues</summary>
        public static event Action Resumed
        {
            add { MessengerBridge.EnsureResumingGame(); lock (Gate) _resumed += value; }
            remove { lock (Gate) _resumed -= value; }
        }

        // ---- Song ------------------------------------------------------------------------------

        private static Action<SongInfo> _songChanged;
        /// <summary>The current song changed. Fires in the menus, well before a ride starts</summary>
        public static event Action<SongInfo> SongChanged
        {
            add { MessengerBridge.EnsureSongChanged(); lock (Gate) _songChanged += value; }
            remove { lock (Gate) _songChanged -= value; }
        }

        private static Action<SongInfo> _songAboutToChange;
        /// <summary>The player picked a song and the game is about to load it</summary>
        public static event Action<SongInfo> SongAboutToChange
        {
            add { MessengerBridge.EnsureAboutToChangeSong(); lock (Gate) _songAboutToChange += value; }
            remove { lock (Gate) _songAboutToChange -= value; }
        }

        private static Action _songStarted;
        /// <summary>The audio began to play</summary>
        public static event Action SongStarted
        {
            add { MessengerBridge.EnsureSongStartedPlaying(); lock (Gate) _songStarted += value; }
            remove { lock (Gate) _songStarted -= value; }
        }

        private static Action _songEnded;
        /// <summary>The audio ran out. See <see cref="RideEnded"/> for the other ways a ride stops</summary>
        public static event Action SongEnded
        {
            add { MessengerBridge.EnsureSongEnded(); lock (Gate) _songEnded += value; }
            remove { lock (Gate) _songEnded -= value; }
        }

        // ---- Scoring, tricks and traffic -------------------------------------------------------

        private static Action<float> _scoreUpdated;
        /// <summary>The running score, as a total. This is the one to use</summary>
        // From ScoreManager.AddPoints, which computes the new total and broadcasts it, so it
        // matches the number on screen. Fires only while a ride is in progress
        public static event Action<float> ScoreUpdated
        {
            add { MessengerBridge.EnsureScoreUpdated(); lock (Gate) _scoreUpdated += value; }
            remove { lock (Gate) _scoreUpdated -= value; }
        }

        private static Action<float, int> _scoreChanged;
        /// <summary>One of the inputs to scoring, and almost certainly not what you want</summary>
        // <see cref="ScoreUpdated"/> is. The first argument is a change, not a total, and it is a
        // partial feed: ScoreManager also adds points from OnTrafficCollected and OnAddPoints
        // without passing through here, so adding these up lands well below the real score
        // Exposed because it says a trick or a floater just scored, not because it accumulates
        // The second argument is the player number, 1-based
        public static event Action<float, int> ScoreChanged
        {
            add { MessengerBridge.EnsureScoreChanged(); lock (Gate) _scoreChanged += value; }
            remove { lock (Gate) _scoreChanged -= value; }
        }

        private static Action<int> _trickStarted;
        /// <summary>A trick began. The argument is the game's trick id</summary>
        public static event Action<int> TrickStarted
        {
            add { MessengerBridge.EnsureTrickStarted(); lock (Gate) _trickStarted += value; }
            remove { lock (Gate) _trickStarted -= value; }
        }

        private static Action<int, int> _trickDone;
        /// <summary>A trick completed: the trick id, then the points it earned</summary>
        public static event Action<int, int> TrickDone
        {
            add { MessengerBridge.EnsureTrickDone(); lock (Gate) _trickDone += value; }
            remove { lock (Gate) _trickDone -= value; }
        }

        private static Action<float> _playerJumped;
        /// <summary>The player jumped. The argument is the jump height</summary>
        public static event Action<float> PlayerJumped
        {
            add { MessengerBridge.EnsurePlayerJumped(); lock (Gate) _playerJumped += value; }
            remove { lock (Gate) _playerJumped -= value; }
        }

        private static Action _playerLanded;
        /// <summary>The player landed cleanly</summary>
        public static event Action PlayerLanded
        {
            add { MessengerBridge.EnsurePlayerLanded(); lock (Gate) _playerLanded += value; }
            remove { lock (Gate) _playerLanded -= value; }
        }

        private static Action _playerCrashLanded;
        /// <summary>The player landed badly</summary>
        public static event Action PlayerCrashLanded
        {
            add { MessengerBridge.EnsurePlayerCrashLanded(); lock (Gate) _playerCrashLanded += value; }
            remove { lock (Gate) _playerCrashLanded -= value; }
        }

        private static Action<int, int> _playerLandedWithPoints;
        /// <summary>A jump finished and scored: the points, then the jump number</summary>
        public static event Action<int, int> PlayerLandedWithPoints
        {
            add { MessengerBridge.EnsurePlayerLandedWithPoints(); lock (Gate) _playerLandedWithPoints += value; }
            remove { lock (Gate) _playerLandedWithPoints -= value; }
        }

        private static Action<int, int, Vector3> _trafficCollected;
        /// <summary>A block was collected: its type, its lane, then its world position</summary>
        // The Vector3 is the one Unity type in any payload here, and only because it is a value
        // type -- a subscriber gets a copy and can write nothing back through it
        // Fires once per collected block, which is often. Keep the handler cheap and allocate
        // nothing in it
        public static event Action<int, int, Vector3> TrafficCollected
        {
            add { MessengerBridge.EnsureTrafficCollected(); lock (Gate) _trafficCollected += value; }
            remove { lock (Gate) _trafficCollected -= value; }
        }

        private static Action<int, int, Vector3> _trafficMissed;
        /// <summary>A block went by uncollected: its type, its lane, then its world position</summary>
        public static event Action<int, int, Vector3> TrafficMissed
        {
            add { MessengerBridge.EnsureTrafficMissed(); lock (Gate) _trafficMissed += value; }
            remove { lock (Gate) _trafficMissed -= value; }
        }

        // ---- Lua diagnostics -------------------------------------------------------------------

        private static Action<string> _luaSkinError;
        /// <summary>A skin script failed</summary>
        // Raised from LuaController.LateUpdate, so a skin that throws inside its own Update
        // produces one of these per frame. Collapse repeats rather than recording each one
        public static event Action<string> LuaSkinError
        {
            add { MessengerBridge.EnsureLuaSkinError(); lock (Gate) _luaSkinError += value; }
            remove { lock (Gate) _luaSkinError -= value; }
        }

        private static Action<string> _luaModError;
        /// <summary>A mode script failed. The same advice about repeats applies</summary>
        public static event Action<string> LuaModError
        {
            add { MessengerBridge.EnsureLuaModError(); lock (Gate) _luaModError += value; }
            remove { lock (Gate) _luaModError -= value; }
        }

        private static Action<string> _luaScriptPrint;
        /// <summary>A skin or mode script printed text</summary>
        public static event Action<string> LuaScriptPrint
        {
            add { MessengerBridge.EnsureLuaSkinPrintText(); lock (Gate) _luaScriptPrint += value; }
            remove { lock (Gate) _luaScriptPrint -= value; }
        }

        // ---- Selection and navigation ----------------------------------------------------------

        private static Action _skinChanged;
        /// <summary>The active skin changed</summary>
        // Not the same as <see cref="AS2Events.SelectionChanged"/>, which fires as the player moves
        // the highlight. This fires when the game adopts a skin, including from outside the selector
        // Carries no payload -- read the new value with <see cref="AS2Events.CurrentKey"/>
        public static event Action SkinChanged
        {
            add { MessengerBridge.EnsureSkinChanged(); lock (Gate) _skinChanged += value; }
            remove { lock (Gate) _skinChanged -= value; }
        }

        private static Action _modeChanged;
        /// <summary>The active mode changed. The note on <see cref="SkinChanged"/> applies</summary>
        public static event Action ModeChanged
        {
            add { MessengerBridge.EnsureModeChanged(); lock (Gate) _modeChanged += value; }
            remove { lock (Gate) _modeChanged -= value; }
        }

        private static Action _musicBrowserOpened;
        /// <summary>The player went to the browse music screen</summary>
        // Fires before the list is built, so it is the moment to refresh anything the browse
        // screen reads off disk
        public static event Action MusicBrowserOpened
        {
            add { MessengerBridge.EnsureBrowseSongsClicked(); lock (Gate) _musicBrowserOpened += value; }
            remove { lock (Gate) _musicBrowserOpened -= value; }
        }

        private static Action _gameShuttingDown;
        /// <summary>The game is closing down gracefully</summary>
        // A last chance to flush. Do not rely on it alone -- OnApplicationQuit and OnDestroy are
        // the ones that always run
        public static event Action GameShuttingDown
        {
            add { MessengerBridge.EnsureShuttingDown(); lock (Gate) _gameShuttingDown += value; }
            remove { lock (Gate) _gameShuttingDown -= value; }
        }

        // ---- Raising (internal) ----------------------------------------------------------------
        //
        // Each raiser passes its delegate field as an argument, which reads the field exactly once.
        // That matters for the same reason it does in AS2Events: a mod may call -= from any thread,
        // and reading the field twice -- once to null-check, once for GetInvocationList -- lets the
        // last removal land between the two and turns GetInvocationList into a NullReferenceException
        // on the game thread. An argument is a snapshot, so a mod that unsubscribes a moment too late
        // gets one more call instead. Make a handler that unsubscribes itself tolerate that.

        internal static void RaiseGameplayStarted() { Raise(_gameplayStarted, "GameplayStarted"); }
        internal static void RaiseRideEnded() { Raise(_rideEnded, "RideEnded"); }
        internal static void RaisePaused() { Raise(_paused, "Paused"); }
        internal static void RaiseResumed() { Raise(_resumed, "Resumed"); }
        internal static void RaiseSongStarted() { Raise(_songStarted, "SongStarted"); }
        internal static void RaiseSongEnded() { Raise(_songEnded, "SongEnded"); }
        internal static void RaisePlayerLanded() { Raise(_playerLanded, "PlayerLanded"); }
        internal static void RaisePlayerCrashLanded() { Raise(_playerCrashLanded, "PlayerCrashLanded"); }
        internal static void RaiseSkinChanged() { Raise(_skinChanged, "SkinChanged"); }
        internal static void RaiseModeChanged() { Raise(_modeChanged, "ModeChanged"); }
        internal static void RaiseMusicBrowserOpened() { Raise(_musicBrowserOpened, "MusicBrowserOpened"); }
        internal static void RaiseGameShuttingDown() { Raise(_gameShuttingDown, "GameShuttingDown"); }

        internal static void RaiseRideScored(RideResult r) { Raise(_rideScored, "RideScored", r); }
        internal static void RaiseTrickStarted(int id) { Raise(_trickStarted, "TrickStarted", id); }
        internal static void RaisePlayerJumped(float h) { Raise(_playerJumped, "PlayerJumped", h); }
        internal static void RaiseScoreUpdated(float total) { Raise(_scoreUpdated, "ScoreUpdated", total); }
        internal static void RaiseSongChanged(SongInfo s) { Raise(_songChanged, "SongChanged", s); }
        internal static void RaiseSongAboutToChange(SongInfo s) { Raise(_songAboutToChange, "SongAboutToChange", s); }
        internal static void RaiseLuaSkinError(string m) { Raise(_luaSkinError, "LuaSkinError", m); }
        internal static void RaiseLuaModError(string m) { Raise(_luaModError, "LuaModError", m); }
        internal static void RaiseLuaScriptPrint(string m) { Raise(_luaScriptPrint, "LuaScriptPrint", m); }

        internal static void RaiseScoreChanged(float d, int p) { Raise(_scoreChanged, "ScoreChanged", d, p); }
        internal static void RaiseTrickDone(int id, int pts) { Raise(_trickDone, "TrickDone", id, pts); }
        internal static void RaisePlayerLandedWithPoints(int pts, int n) { Raise(_playerLandedWithPoints, "PlayerLandedWithPoints", pts, n); }

        internal static void RaiseTrafficCollected(int t, int lane, Vector3 at) { Raise(_trafficCollected, "TrafficCollected", t, lane, at); }
        internal static void RaiseTrafficMissed(int t, int lane, Vector3 at) { Raise(_trafficMissed, "TrafficMissed", t, lane, at); }

        private static void Raise(Action subscribers, string name)
        {
            if (subscribers == null) return;
            foreach (Action h in subscribers.GetInvocationList()) Safe(name, h);
        }

        private static void Raise<T>(Action<T> subscribers, string name, T arg)
        {
            if (subscribers == null) return;
            foreach (Action<T> h in subscribers.GetInvocationList())
            {
                Action<T> handler = h;
                Safe(name, delegate { handler(arg); });
            }
        }

        private static void Raise<T, U>(Action<T, U> subscribers, string name, T a, U b)
        {
            if (subscribers == null) return;
            foreach (Action<T, U> h in subscribers.GetInvocationList())
            {
                Action<T, U> handler = h;
                Safe(name, delegate { handler(a, b); });
            }
        }

        private static void Raise<T, U, V>(Action<T, U, V> subscribers, string name, T a, U b, V c)
        {
            if (subscribers == null) return;
            foreach (Action<T, U, V> h in subscribers.GetInvocationList())
            {
                Action<T, U, V> handler = h;
                Safe(name, delegate { handler(a, b, c); });
            }
        }

        /// <summary>Runs one subscriber. A mod that throws in a handler breaks only itself</summary>
        private static void Safe(string name, Action body)
        {
            try { body(); }
            catch (Exception e) { ModApiPlugin.Log.LogError("A subscriber to " + name + " threw: " + e); }
        }
    }
}
