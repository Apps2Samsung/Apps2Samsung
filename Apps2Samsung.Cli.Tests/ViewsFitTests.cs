using Spectre.Console;

namespace Apps2Samsung.Cli.Tests;


public sealed class ViewsFitTests
{
    private static int Cells(string markup) => Views.Cells(Markup.Remove(markup));

    [Fact]
    public void A_line_that_fits_is_untouched()
    {
        var line = "[bold]Native legacy[/]  v2.0.8";
        Assert.Same(line, Views.Fit(line, 30, unicode: true));
        Assert.Same(line, Views.Fit(line, Cells(line), unicode: true));
    }

    [Fact]
    public void A_long_plain_line_ends_in_an_ellipsis_within_the_width()
    {
        var cut = Views.Fit("Native legacy  WebAssembly (Tizen 5.5+)  Nuvio-Legacy.wgt  v2.0.8  Tizen 5.5+", 40, unicode: true);
        Assert.Equal(40, Cells(cut));
        Assert.EndsWith("…[/]", cut);
        Assert.StartsWith("Native legacy  WebAssembly (Tizen 5.5+)", cut);
    }

    [Fact]
    public void Without_unicode_the_ellipsis_is_three_dots()
    {
        var cut = Views.Fit("Native legacy  WebAssembly (Tizen 5.5+)  Nuvio-Legacy.wgt  v2.0.8  Tizen 5.5+", 40, unicode: false);
        Assert.Equal(40, Cells(cut));
        Assert.EndsWith("...[/]", cut);
    }

    [Fact]
    public void Open_tags_are_closed_and_the_markup_still_parses()
    {
        var line = $"[bold]Nuvio[/] [{Views.MutedHex}]Unofficial native C/SDL2 Nuvio client, fast on older TVs[/]";
        var cut = Views.Fit(line, 24, unicode: true);
        Assert.Equal(24, Cells(cut));
        Assert.Contains("[/]", cut);
        _ = new Markup(cut); // throws on an unbalanced tag
        Assert.StartsWith("[bold]Nuvio[/] [", cut);
    }

    [Fact]
    public void Escaped_brackets_count_as_one_cell_and_are_never_split()
    {
        var line = Markup.Escape("[TV] Samsung 4 Series (32) UN32T4300AKXZL");
        var cut = Views.Fit(line, 12, unicode: true);
        Assert.Equal(12, Cells(cut));
        Assert.StartsWith("[[TV]] Samsun", cut);
        _ = new Markup(cut);
    }

    [Fact]
    public void A_wide_character_costs_two_cells()
    {
        Assert.Equal(13, Views.Cells("Douyin (抖音)"));
        var line = Markup.Escape("Douyin (抖音)  v1.4.6  streaming");
        var cut = Views.Fit(line, 12, unicode: true);
        Assert.True(Cells(cut) <= 12, cut);
        _ = new Markup(cut);
    }

    [Fact]
    public void Trailing_spaces_before_the_cut_are_dropped()
    {
        var cut = Views.Fit("Jellyfin - AVPlay - 10.10z SmartHub  v0.1.0", 29, unicode: true);
        Assert.DoesNotContain(" …", cut);
        Assert.True(Cells(cut) <= 29, cut);
    }

    [Fact]
    public void No_width_gives_nothing()
    {
        Assert.Equal(string.Empty, Views.Fit("anything", 0, unicode: true));
    }
}
