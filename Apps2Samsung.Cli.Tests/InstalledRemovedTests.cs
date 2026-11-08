using Apps2Samsung.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Apps2Samsung.Cli.Tests;

/// <summary>An installed app the catalog removed (#1033): the listing says so, with no update offered.</summary>
public sealed class InstalledRemovedTests
{
    private static readonly InstalledCommand.RemovedInfo Gone = new("Bisnor", "unlicensed-content",
        "Removed: it ships streams or links to content it has no licence for.", "Gets its streams from unlicensed film servers.", "2026-11-08");

    private static InstalledCommand.Row Row() => new("Bisnor", "hnn00bisnr.BisnorCinema", "1.4", "", true,
        CatalogApp: null, CatalogFile: null, CatalogVersion: null, State: null, Removed: Gone);

    private static string Render(IRenderable view, int width)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Write(view);
        return System.Text.RegularExpressions.Regex.Replace(writer.ToString(), @"\s+", " ");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void A_removed_app_shows_the_badge_reason_and_note_and_no_update(int width)
    {
        var text = Render(Views.InstalledApps(new[] { Row() }, "192.168.1.8", width, unicode: true), width);

        Assert.Contains("Removed", text);
        Assert.Contains("licence", text);
        Assert.Contains("film servers", text);
        Assert.DoesNotContain("update", text.Replace("updates from", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_removed_app_gives_no_install_line_for_an_update()
    {
        Assert.Null(Row().State);
        Assert.Null(Row().CatalogApp);
    }
}
