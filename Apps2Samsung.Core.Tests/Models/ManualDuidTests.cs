using Apps2Samsung.Models;
using Xunit;

namespace Apps2Samsung.Tests.Models;

public class ManualDuidTests
{
    [Theory]
    [InlineData("2DCKJITTLDPSA")]
    [InlineData("  2DCKJITTLDPSA  ")]
    [InlineData("2DCKJITTLDPSA\n")]
    public void OneSuppliedDuidIsTheFallback(string setting) =>
        Assert.Equal("2DCKJITTLDPSA", ManualDuid.Only(setting));

    // The setting also lists extra TVs to authorize in the certificate (#1103 review): with two or
    // more entries we cannot tell which one is the TV being installed to, so there is no fallback.
    [Theory]
    [InlineData("2DCKJITTLDPSA,JHCLFXOSWXYAE")]
    [InlineData("2DCKJITTLDPSA\nJHCLFXOSWXYAE")]
    [InlineData("2DCKJITTLDPSA; JHCLFXOSWXYAE")]
    public void SeveralSuppliedDuidsAreNotAGuess(string setting) =>
        Assert.Null(ManualDuid.Only(setting));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingSuppliedIsNoFallback(string? setting) =>
        Assert.Null(ManualDuid.Only(setting));

    // A failed read can end up in Settings pasted as error text; that must not reach a certificate.
    [Theory]
    [InlineData("Failed open device: Operation not permitted")]
    [InlineData("error: closed")]
    public void ErrorTextIsNotADuid(string setting) =>
        Assert.Null(ManualDuid.Only(setting));

    [Theory]
    [InlineData("a,b", new[] { "a", "b" })]
    [InlineData("a;b\nc  d", new[] { "a", "b", "c", "d" })]
    [InlineData("  a  ", new[] { "a" })]
    public void SplitOnEverySeparatorTheSettingAccepts(string setting, string[] expected) =>
        Assert.Equal(expected, ManualDuid.Split(setting));

    [Fact]
    public void SplitOfNothingIsEmpty() =>
        Assert.Empty(ManualDuid.Split(null));
}
