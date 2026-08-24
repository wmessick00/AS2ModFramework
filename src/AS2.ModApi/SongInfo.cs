namespace AS2.ModApi
{
    /// <summary>What this framework tells a mod about a song</summary>
    // The game's own Song reaches the bridge and never leaves it. Two reasons:
    // Read-only in fact, not by convention -- Song's every member is a public mutable field, so
    // handing one over hands out a writeable view of the game's state
    // A patch update then cannot break the event. Game members resolve by name and tolerate one
    // going missing, where a game type in a public signature costs a compile break in every mod
    // A struct with get-only properties: no null to check, no setter to write
    // Why the copies, in full: see the RideResult and SongInfo wiki page
    public struct SongInfo
    {
        /// <summary>Song title as the game holds it, or null</summary>
        public string Title { get; }

        /// <summary>Artist name, or null. The game keeps this on a separate `Artist` object</summary>
        public string Artist { get; }

        /// <summary>
        /// File path, or a "youtube:&lt;id&gt;" pseudo-path for a stream. May be null.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// The game's own stable song id, or null when it could not be computed.
        ///
        /// The game derives this from the audio file's content -- an MD5 of the bytes -- so it
        /// survives a rename or a move, and two copies of one file share it. It is null when the
        /// path is neither a YouTube pseudo-path nor a file that still exists.
        /// </summary>
        public string Identifier { get; }

        /// <summary>Song length in seconds, or 0 when the game has not worked it out yet</summary>
        public float DurationSeconds { get; }

        /// <summary>True when this is a YouTube stream rather than a local file</summary>
        public bool IsYouTube { get; }

        internal SongInfo(string title, string artist, string path, string identifier,
                          float durationSeconds, bool isYouTube)
        {
            Title = title;
            Artist = artist;
            Path = path;
            Identifier = identifier;
            DurationSeconds = durationSeconds;
            IsYouTube = isYouTube;
        }

        /// <summary>
        /// The best available stable key for storing data about this song.
        ///
        /// Prefers <see cref="Identifier"/> and falls back to <see cref="Path"/>, so a song whose
        /// file has gone missing still groups with itself. Null only when the framework knows
        /// nothing about the song at all, which a caller should treat as "do not record this ride".
        /// </summary>
        public string Key
        {
            get
            {
                if (!string.IsNullOrEmpty(Identifier)) return Identifier;
                if (!string.IsNullOrEmpty(Path)) return Path;
                return null;
            }
        }

        /// <summary>"Artist - Title", or whichever half exists, or the path</summary>
        public string Display
        {
            get
            {
                bool hasArtist = !Str.IsBlank(Artist);
                bool hasTitle = !Str.IsBlank(Title);
                if (hasArtist && hasTitle) return Artist + " - " + Title;
                if (hasTitle) return Title;
                if (hasArtist) return Artist;
                return Path ?? "(unknown song)";
            }
        }

        public override string ToString() { return Display; }
    }
}
