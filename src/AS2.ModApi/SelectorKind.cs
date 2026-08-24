namespace AS2.ModApi
{
    /// <summary>Which of the game's two selector screens something refers to</summary>
    // Its own file, not beside AS2Events, so TargetResolver compiles into the test project
    // without pulling in LuaInterface and Unity as well (see tests/AS2.ModApi.Tests)
    public enum SelectorKind
    {
        Skin,
        Mode
    }
}
