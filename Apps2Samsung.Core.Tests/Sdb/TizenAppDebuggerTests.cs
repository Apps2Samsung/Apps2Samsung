using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

public class TizenAppDebuggerTests
{
    [Theory]
    // Tizen 3.0 (2017) and older only answer the suffixed form; the bare one gets the channel closed (#954).
    [InlineData("3.0", true)]
    [InlineData("2.4", true)]
    [InlineData("3.0.0", true)]
    // From 4.0 on the bare form is the one Tizen Studio sends.
    [InlineData("4.0", false)]
    [InlineData("5.0", false)]
    [InlineData("10.0", false)]
    public void UsesLegacyDebugForm_FollowsTheTizenVersion(string platformVersion, bool expected)
    {
        Assert.Equal(expected, TizenAppDebugger.UsesLegacyDebugForm(Version.Parse(platformVersion)));
    }

    [Fact]
    public void UsesLegacyDebugForm_UnknownVersion_StartsWithTheBareForm()
    {
        Assert.False(TizenAppDebugger.UsesLegacyDebugForm(null));
    }

    [Fact]
    public void DebugCommand_BareForm_IsTheVerbAndTheId()
    {
        Assert.Equal("0 debug A2STVStore.Store", TizenAppDebugger.DebugCommand("A2STVStore.Store", legacyForm: false));
    }

    [Fact]
    public void DebugCommand_LegacyForm_CarriesTheWait()
    {
        Assert.Equal($"0 debug A2STVStore.Store {TizenAppDebugger.LegacyDebugWait}", TizenAppDebugger.DebugCommand("A2STVStore.Store", legacyForm: true));
    }
}
