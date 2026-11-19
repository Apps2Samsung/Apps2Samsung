using Apps2Samsung.Cli.Commands;

namespace Apps2Samsung.Cli.Tests;

/// <summary>The command line's manners (#1128): what a mistyped verb, a missing value and a shown token do.</summary>
public sealed class CommandLineMannersTests
{
    [Theory]
    [InlineData("instal", "install")]
    [InlineData("unistall", "uninstall")]
    [InlineData("catalogue", "catalog")]
    [InlineData("scna", "scan")]
    public void A_mistyped_verb_names_the_close_one(string typed, string meant)
    {
        var message = Dispatch.Unknown(typed);
        Assert.Contains($"Did you mean '{meant}'?", message);
        Assert.DoesNotContain("\n", message);
    }

    [Fact]
    public void A_verb_nothing_like_any_gets_no_guess()
    {
        Assert.DoesNotContain("Did you mean", Dispatch.Unknown("frobnicate"));
    }

    [Fact]
    public void An_option_is_not_the_value_of_the_option_before_it()
    {
        var (_, error) = CliOptions.Parse(new[] { "install", "app", "--tv", "--yes" });
        Assert.Equal("--tv needs the TV's IP address", error);
    }

    [Theory]
    [InlineData("--github-token")]
    [InlineData("--duids")]
    public void A_valued_option_without_its_value_is_an_error(string option)
    {
        var (_, error) = CliOptions.Parse(new[] { "catalog", option });
        Assert.NotNull(error);
    }

    [Fact]
    public void Short_version_flag_works_after_a_verb_and_the_long_one_still_takes_a_tag()
    {
        var (o, error) = CliOptions.Parse(new[] { "scan", "-V" });
        Assert.Null(error);
        Assert.True(o.ShowVersion);

        (o, error) = CliOptions.Parse(new[] { "install", "app", "--version", "v1.2.3" });
        Assert.Null(error);
        Assert.False(o.ShowVersion);
        Assert.Equal("v1.2.3", o.Version);
    }

    [Fact]
    public void A_token_is_not_shown()
    {
        var shown = CliOptions.WithoutSecrets(new[] { "--adult", "--github-token", "ghp_secret", "--data-dir", "x" }).ToList();
        Assert.Equal(new[] { "--adult", "--github-token", "<token>", "--data-dir", "x" }, shown);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("installed")]
    [InlineData("uninstall")]
    [InlineData("launch")]
    [InlineData("menu")]
    public void Every_verb_has_its_own_help(string verb)
    {
        Assert.NotEqual(Help.Usage, Help.For(verb));
        Assert.Contains($"apps2samsung-cli {verb}", Help.For(verb));
    }
}
