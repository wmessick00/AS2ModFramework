using System;
using System.Reflection;
using HarmonyLib;

namespace AS2.ModApi
{
    /// <summary>
    /// Copies the game's end-of-ride totals into a <see cref="RideResult"/>.
    ///
    /// Every member is resolved by name through AccessTools and read with FieldInfo.GetValue. There
    /// is no SetValue anywhere in this file and no compile-time reference to ScorecardFinal, for the
    /// same reason Patches.cs resolves its targets by name: the community patch ships a modified
    /// Assembly-CSharp, and a member that moves should cost one null field and one warning rather
    /// than the whole event.
    ///
    /// <para>
    /// <b>Song.GetIdentifier() is deliberately not called.</b> It looks like the obvious way to get
    /// a stable song key, and it is a trap twice over. It memoises its answer into the song's own
    /// `identifier` field, so calling it writes to a game object -- which is exactly what this
    /// framework promises not to do. And on a miss it computes an MD5 over the entire audio file,
    /// on the game thread, inside a Messenger broadcast that has no exception handler. So this reads
    /// the `identifier` field as the game left it and lets <see cref="SongInfo.Key"/> fall back to
    /// the path when it is null.
    /// </para>
    /// </summary>
    internal static class ScorecardReader
    {
        private static bool _resolved;
        private static bool _usable;

        private static FieldInfo _songJustScored;
        private static FieldInfo _songSeconds;
        private static FieldInfo _greysHit;
        private static FieldInfo _colorsHit;
        private static FieldInfo _numJumps;
        private static FieldInfo _totalTrickPoints;
        private static FieldInfo _scoreFinalizedByLua;

        /// <summary>
        /// Builds the result for the ride that just ended. `score` is the value the game passed to
        /// the SendingRideScore broadcast, which is authoritative; the rest is best-effort, and a
        /// counter the game did not report comes back as -1 rather than as a plausible 0.
        /// </summary>
        internal static RideResult Read(int score)
        {
            Resolve();
            if (!_usable) return new RideResult(default(SongInfo), score, -1, -1, -1, -1, 0f, false);

            return new RideResult(
                Describe(ReadValue(_songJustScored) as Song),
                score,
                ReadInt(_greysHit),
                ReadInt(_colorsHit),
                ReadInt(_numJumps),
                ReadInt(_totalTrickPoints),
                ReadFloat(_songSeconds),
                ReadBool(_scoreFinalizedByLua));
        }

        /// <summary>
        /// Copies one of the game's Song objects into our own struct. Field reads only -- every
        /// member used here is a public field on Song, so nothing in this method is a call into the
        /// game and nothing can write back.
        /// </summary>
        internal static SongInfo Describe(Song song)
        {
            if (song == null) return default(SongInfo);

            try
            {
                string path = song.Path;
                string artist = song.artist != null ? song.artist.Name : null;
                bool isYouTube = !Str.IsBlank(path)
                                 && path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase);

                return new SongInfo(song.Name, artist, path, song.identifier,
                                    song.DurationSeconds, isYouTube);
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not read the current song: " + e.Message);
                return default(SongInfo);
            }
        }

        /// <summary>
        /// Looks the scorecard up once. ScorecardFinal is a static-only class, so there is no
        /// instance to find and no lifetime to track -- but it is also absent from the probe dump in
        /// docs/reference, so treat it as less certain than the types Patches.cs targets.
        /// </summary>
        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                Type t = AccessTools.TypeByName("ScorecardFinal");
                if (t == null)
                {
                    ModApiPlugin.Log.LogWarning(
                        "ScorecardFinal not found in this build; RideScored will carry the score only.");
                    return;
                }

                // ScorecardFinal.finalScore is deliberately not read. The score comes from the
                // SendingRideScore broadcast, which is authoritative, so Read() takes it as a
                // parameter and the field would only be a second, later answer to the same question.
                _songJustScored = AccessTools.Field(t, "songJustScored");
                _songSeconds = AccessTools.Field(t, "songSeconds");
                _greysHit = AccessTools.Field(t, "greysHit");
                _colorsHit = AccessTools.Field(t, "colorsHit");
                _numJumps = AccessTools.Field(t, "numJumps");
                _totalTrickPoints = AccessTools.Field(t, "totalTrickPoints");
                _scoreFinalizedByLua = AccessTools.Field(t, "scoreFinalizedByLua");

                _usable = true;
            }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not resolve ScorecardFinal: " + e.Message);
            }
        }

        // ---- Reads ---------------------------------------------------------------------------
        //
        // GetValue only. A missing field is a null FieldInfo rather than a throw, because AccessTools
        // returns null for a name it cannot find, so each of these has to tolerate one.

        private static object ReadValue(FieldInfo field)
        {
            if (field == null) return null;
            try { return field.GetValue(null); }
            catch (Exception e)
            {
                ModApiPlugin.Log.LogWarning("Could not read ScorecardFinal." + field.Name + ": " + e.Message);
                return null;
            }
        }

        private static int ReadInt(FieldInfo field)
        {
            object v = ReadValue(field);
            if (v is int) return (int)v;
            if (v is float) return (int)(float)v;
            return -1;
        }

        private static float ReadFloat(FieldInfo field)
        {
            object v = ReadValue(field);
            if (v is float) return (float)v;
            if (v is int) return (int)v;
            return 0f;
        }

        private static bool ReadBool(FieldInfo field)
        {
            object v = ReadValue(field);
            return v is bool && (bool)v;
        }
    }
}
