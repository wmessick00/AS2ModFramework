namespace AS2.ModApi
{
    /// <summary>Small .NET 3.5 gap-fillers</summary>
    // Unity 2017's legacy Mono profile, so nothing added in .NET 4 exists here
    // No string.IsNullOrWhiteSpace, no Task, no ValueTuple
    // Its own file, away from ModApiPlugin, so the path and key logic compiles into the test
    // project without dragging in BepInEx and Unity (see tests/AS2.ModApi.Tests)
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
