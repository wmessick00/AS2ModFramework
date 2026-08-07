namespace AS2.ModApi
{
    /// <summary>
    /// Small .NET 3.5 gap-fillers. The game runs Unity 2017's legacy Mono profile, so anything
    /// added in .NET 4 is unavailable -- no string.IsNullOrWhiteSpace, no Task, no ValueTuple.
    ///
    /// Kept in its own file, away from ModApiPlugin, so that the path and key logic which depends
    /// on it can be compiled into the test project without dragging in BepInEx and Unity. See
    /// tests/AS2.ModApi.Tests.
    /// </summary>
    internal static class Str
    {
        public static bool IsBlank(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            for (int i = 0; i < s.Length; i++)
                if (!char.IsWhiteSpace(s[i])) return false;
            return true;
        }
    }
}
