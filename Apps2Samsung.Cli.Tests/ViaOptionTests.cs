namespace Apps2Samsung.Cli.Tests;

public sealed class ViaOptionTests
{
    [Theory]
    [InlineData("tvstore", true)]
    [InlineData("tv-store", true)]
    [InlineData("sdb", false)]
    public void Via_picks_the_way_in(string value, bool tvStore)
    {
        var (o, error) = CliOptions.Parse(new[] { "install", "app.wgt", "--tv", "192.168.1.50", "--via", value });
        Assert.Null(error);
        Assert.Equal(tvStore, o.ViaTvStore);
    }

    [Fact]
    public void Via_refuses_anything_else()
    {
        var (_, error) = CliOptions.Parse(new[] { "install", "app.wgt", "--via", "usb" });
        Assert.NotNull(error);
    }
}
