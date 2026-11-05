namespace Apps2Samsung.Cli.Tests;

public sealed class LeftoversOptionTests
{
    [Fact]
    public void Clear_IsAFlag()
    {
        var (o, error) = CliOptions.Parse(new[] { "leftovers", "--tv", "192.168.1.8", "--clear" });
        Assert.Null(error);
        Assert.Equal("leftovers", o.Command);
        Assert.True(o.Clear);
        Assert.Null(o.DeleteFile);
    }

    [Fact]
    public void Delete_TakesTheFileName()
    {
        var (o, error) = CliOptions.Parse(new[] { "leftovers", "--tv", "192.168.1.8", "--delete", "Litefin-2.0.30-Tizen-Normal.wgt" });
        Assert.Null(error);
        Assert.False(o.Clear);
        Assert.Equal("Litefin-2.0.30-Tizen-Normal.wgt", o.DeleteFile);
        // The menu shows and re-runs the line; the flag has to survive the round trip.
        Assert.Contains("--delete", o.Passthrough);
    }

    [Fact]
    public void Delete_WithoutAName_IsAUsageError()
    {
        var (_, error) = CliOptions.Parse(new[] { "leftovers", "--tv", "192.168.1.8", "--delete" });
        Assert.NotNull(error);
    }
}
