namespace AS2.ModApi
{
    /// <summary>
    /// Which of the game's two selector screens something refers to.
    ///
    /// In its own file rather than beside <see cref="AS2Events"/> so that TargetResolver, which
    /// needs it, can be compiled into the test project without also pulling in LuaInterface and
    /// Unity. See tests/AS2.ModApi.Tests.
    /// </summary>
    public enum SelectorKind
    {
        Skin,
        Mode
    }
}
