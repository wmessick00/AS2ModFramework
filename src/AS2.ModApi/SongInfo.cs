namespace AS2.ModApi
{
    /// <summary>
    /// What this framework will tell a mod about a song.
    ///
    /// The game has its own `Song` class and the bridge receives one, but that object never leaves
    /// <see cref="MessengerBridge"/>. Two reasons, and the second is the one that matters.
    ///
    /// <para>
    /// It keeps the game events read-only in fact rather than by convention. `Song` is a reference
    /// type whose every member is a public mutable field, so handing one to a subscriber would hand
    /// it a writeable view of the game's state. A copy cannot be written back through.
    /// </para>
    ///
    /// <para>
    /// It also keeps a community patch update from breaking the event. The framework resolves game
    /// members by name and tolerates one going missing; a game type in a public signature would
    /// instead cost a compile break in every downstream mod. See the note on AccessTools at the top
    /// of Patches.cs.
    /// </para>
    ///
    /// <para>
    /// A struct with get-only properties rather than a class: there is no null to check, and no
    /// setter to write. An absent song is a default value whose <see cref="Key"/> is null.
    /// </para>
    /// </summary>
    public struct SongInfo
    {
        /// <summary>Song title as the game holds it, or null.</summary>
        public string Title { get; }

        /// <summary>Artist name, or null. The game keeps this on a separate `Artist` object.</summary>
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

        /// <summary>Song length in seconds, or 0 when the game has not worked it out yet.</summary>
        public float DurationSeconds { get; }

        /// <summary>True when this is a YouTube stream rather than a local file.</summary>
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

        /// <summary>"Artist - Title", or whichever half exists, or the path.</summary>
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
