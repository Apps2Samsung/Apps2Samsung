namespace Apps2Samsung.Cli.Tests;

/// <summary>--tizen-cert and --tizen-chain (#988): the flags, the optional level, and the help that mentions them.</summary>
public sealed class TizenCertOptionsTests
{
    [Fact]
    public void Tizen_cert_is_a_flag_without_a_level()
    {
        var (o, error) = CliOptions.Parse(new[] { "sign", "app.wgt", "-o", "out.wgt", "--tizen-cert" });
        Assert.Null(error);
        Assert.True(o.TizenCert);
        Assert.Null(o.TizenLevel);
        Assert.Null(o.TizenChain);
        Assert.Equal(new[] { "app.wgt" }, o.Arguments);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("Partner")]
    [InlineData("platform")]
    public void Tizen_cert_takes_a_level_right_after_it(string level)
    {
        var (o, error) = CliOptions.Parse(new[] { "cert", "--prepare", "--tizen-cert", level });
        Assert.Null(error);
        Assert.True(o.TizenCert);
        Assert.Equal(level.ToLowerInvariant(), o.TizenLevel);
        Assert.Empty(o.Arguments);
    }

    [Fact]
    public void A_file_after_the_flag_is_not_a_level()
    {
        var (o, error) = CliOptions.Parse(new[] { "install", "--tizen-cert", "app.wgt", "--tv", "192.168.1.50" });
        Assert.Null(error);
        Assert.True(o.TizenCert);
        Assert.Null(o.TizenLevel);
        Assert.Equal(new[] { "app.wgt" }, o.Arguments);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("New")]
    public void Tizen_chain_takes_legacy_or_new(string chain)
    {
        var (o, error) = CliOptions.Parse(new[] { "cert", "--prepare", "--tizen-cert", "--tizen-chain", chain });
        Assert.Null(error);
        Assert.Equal(chain.ToLowerInvariant(), o.TizenChain);
    }

    [Fact]
    public void Tizen_chain_refuses_anything_else()
    {
        var (_, error) = CliOptions.Parse(new[] { "cert", "--tizen-chain", "latest" });
        Assert.NotNull(error);
    }

    [Fact]
    public void The_help_mentions_them()
    {
        Assert.Contains("--tizen-cert", Help.For("install"));
        Assert.Contains("--tizen-chain", Help.For("install"));
        Assert.Contains("--tizen-cert", Help.For("sign"));
        Assert.Contains("--tizen-cert", Help.For("cert"));
    }
}
