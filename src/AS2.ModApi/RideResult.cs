namespace AS2.ModApi
{
    // Scoring order, from Game.OnSongEnded
    // ===========================================================================================
    // 1. broadcast "RequestFinalScoring"      score does not exist yet
    // 2. ScorecardFinal.FinalizeScore         score exists from here
    // 3. broadcast "EndCleanup"               -> AS2GameEvents.RideEnded, carries no result
    // 4. broadcast "SendingRideScore"         -> AS2GameEvents.RideScored, carries the score
    // A mod that reads a scorecard at step 1 reads a stale one

    /// <summary>The scorecard for a ride that just finished, copied out of the game's totals</summary>
    // Every field is read through AccessTools and FieldInfo.GetValue, never written, and never
    // handed out as the game's own object (see <see cref="ScorecardReader"/>)
    public struct RideResult
    {
        /// <summary>The song this ride was on. Its <see cref="SongInfo.Key"/> may be null</summary>
        public SongInfo Song { get; }

        /// <summary>Final score, as the game sent it to the leaderboard</summary>
        public int Score { get; }

        /// <summary>Grey blocks hit, or -1 when the game did not report it</summary>
        public int GreysHit { get; }

        /// <summary>Coloured blocks collected, or -1 when the game did not report it</summary>
        public int ColorsHit { get; }

        /// <summary>Jumps taken, or -1 when the game did not report it</summary>
        public int NumJumps { get; }

        /// <summary>Points from tricks, or -1 when the game did not report it</summary>
        public int TotalTrickPoints { get; }

        /// <summary>Length of the ride in seconds, or 0 when the game did not report it</summary>
        public float SongSeconds { get; }

        /// <summary>True when a mode script finalised the score itself, not the game</summary>
        // A designed game feature: Lua gets SetLocalScore and SetGlobalScore, and the game keeps a
        // ScoreManager.modInChargeOfScoring flag
        // A mod comparing scores across rides must not compare one of these against a normal ride
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
