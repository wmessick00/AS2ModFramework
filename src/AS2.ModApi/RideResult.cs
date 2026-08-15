namespace AS2.ModApi
{
    /// <summary>
    /// The scorecard for a ride that has just finished, copied out of the game's own totals.
    ///
    /// <para>
    /// <b>Timing is the whole trick here, so it is worth stating exactly.</b> Game.OnSongEnded does
    /// four things in this order: it broadcasts "RequestFinalScoring", then calls
    /// ScorecardFinal.FinalizeScore, then broadcasts "EndCleanup", then broadcasts
    /// "SendingRideScore" with the final score. Only the last two run after the score exists, and
    /// only the last one carries it. That is why AS2GameEvents.RideScored maps to SendingRideScore
    /// and AS2GameEvents.RideEnded (EndCleanup) carries no result: a mod that reads a scorecard at
    /// RequestFinalScoring time reads a stale one.
    /// </para>
    ///
    /// <para>
    /// Every field is read through AccessTools and FieldInfo.GetValue, never written, and never
    /// exposed as the game's own object. See <see cref="ScorecardReader"/>.
    /// </para>
    /// </summary>
    public struct RideResult
    {
        /// <summary>The song this ride was on. Its <see cref="SongInfo.Key"/> may be null.</summary>
        public SongInfo Song { get; }

        /// <summary>Final score, as the game sent it to the leaderboard.</summary>
        public int Score { get; }

        /// <summary>Grey blocks hit, or -1 when the game did not report it.</summary>
        public int GreysHit { get; }

        /// <summary>Coloured blocks collected, or -1 when the game did not report it.</summary>
        public int ColorsHit { get; }

        /// <summary>Jumps taken, or -1 when the game did not report it.</summary>
        public int NumJumps { get; }

        /// <summary>Points from tricks, or -1 when the game did not report it.</summary>
        public int TotalTrickPoints { get; }

        /// <summary>Length of the ride in seconds, or 0 when the game did not report it.</summary>
        public float SongSeconds { get; }

        /// <summary>
        /// True when a mode script finalised the score itself rather than the game doing it.
        ///
        /// Custom modes scoring themselves is a designed game feature -- the game gives Lua
        /// SetLocalScore and SetGlobalScore and keeps a ScoreManager.modInChargeOfScoring flag. A
        /// mod that compares scores across rides should not compare one of these against a
        /// normally-scored ride.
        /// </summary>
        public bool ScoredByMode { get; }

        internal RideResult(SongInfo song, int score, int greysHit, int colorsHit, int numJumps,
                            int totalTrickPoints, float songSeconds, bool scoredByMode)
        {
            Song = song;
            Score = score;
            GreysHit = greysHit;
            ColorsHit = colorsHit;
            NumJumps = numJumps;
            TotalTrickPoints = totalTrickPoints;
            SongSeconds = songSeconds;
            ScoredByMode = scoredByMode;
        }

        public override string ToString()
        {
            return Song.Display + ": " + Score;
        }
    }
}
