using Apps2Samsung.Models;
using Xunit;

namespace Apps2Samsung.Tests.Sdb;

public class TizenDuidTests
{
    [Fact]
    public void ADuidAloneIsTheDuid() =>
        Assert.Equal("2DCKJITTLDPSA", TizenDuid.FromSdbOutput("2DCKJITTLDPSA\n"));

    // #1081: a 2024 Tizen 8.0 set answers with a warning line first.
    [Theory]
    [InlineData("Failed open device: Operation not permitted\nJHCLFXOSWXYAE")]
    [InlineData("Failed open device: Operation not permitted\r\nJHCLFXOSWXYAE\r\n")]
    [InlineData("\n  Failed open device: Operation not permitted\n\n  JHCLFXOSWXYAE  \n")]
    public void AWarningLineInFrontIsSkipped(string output) =>
        Assert.Equal("JHCLFXOSWXYAE", TizenDuid.FromSdbOutput(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("error: closed")]
    [InlineData("Failed open device: Operation not permitted")]
    [InlineData("SHORT")]
    public void NoDuidLineGivesEmpty(string? output) =>
        Assert.Equal(string.Empty, TizenDuid.FromSdbOutput(output));

    [Fact]
    public void FirstLineSaysWhatCameBackInstead() =>
        Assert.Equal("error: closed", TizenDuid.FirstLine("\n error: closed \nmore"));
}
