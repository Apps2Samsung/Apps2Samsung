namespace Apps2Samsung.Cli.Tests;

/// <summary>Where the CLI takes the terminal's size from (#975): the environment, then stty when .NET's answer is suspect.</summary>
public sealed class TerminalSizeTests
{
    [Theory]
    [InlineData("24 80\n", 80, 24)]
    [InlineData("  52   79  ", 79, 52)]
    public void Stty_output_is_rows_then_columns(string output, int cols, int rows)
    {
        Assert.Equal((cols, rows), TerminalSize.Parse(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0 0")]
    [InlineData("stty: 'standard input': Inappropriate ioctl for device")]
    [InlineData("24")]
    public void Anything_else_from_stty_is_ignored(string? output)
    {
        Assert.Null(TerminalSize.Parse(output));
    }

    [Fact]
    public void The_environment_wins_over_everything()
    {
        Assert.Equal((76, 40), TerminalSize.Resolve((76, 40), (79, 52), (80, 24)));
    }

    [Fact]
    public void Stty_is_pinned_only_when_it_disagrees_with_dotnet()
    {
        Assert.Equal((79, 52), TerminalSize.Resolve(null, (79, 52), (80, 24)));
        Assert.Null(TerminalSize.Resolve(null, (80, 24), (80, 24)));
    }

    [Fact]
    public void Without_other_sources_dotnet_is_followed()
    {
        Assert.Null(TerminalSize.Resolve(null, null, (80, 24)));
    }
}
