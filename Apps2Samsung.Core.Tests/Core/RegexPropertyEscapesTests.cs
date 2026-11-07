using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Tests.Core;

/// <summary>
/// Unicode property escapes in plugin regexes, rewritten for TVs before Chrome 64 (esbuild keeps
/// them at es2015). The cases are the two lines from Jellyfin Enhanced's Media Tags redesign plus the
/// places a regex literal is easy to misread.
/// </summary>
public class RegexPropertyEscapesTests
{
    [Fact]
    public void Jellyfin_Enhanced_logotags_line_loses_its_property_escapes()
    {
        const string js = "const words = text.split(/[\\s\\-—–/&+]+/).filter((w) => /[\\p{L}\\p{N}]/u.test(w));";
        var lowered = RegexPropertyEscapes.Lower(js);

        Assert.DoesNotContain("\\p{", lowered);
        Assert.StartsWith("const words = text.split(/[\\s\\-—–/&+]+/).filter((w) => /[\\u0041-\\u005A\\u0061-\\u007A", lowered);
        Assert.Contains("\\u0030-\\u0039", lowered);
        Assert.EndsWith("]/u.test(w));", lowered);
    }

    [Fact]
    public void Jellyfin_Enhanced_custom_rules_line_keeps_everything_around_the_regex()
    {
        const string js = "return String(text || \"\").replace(/[\\p{White_Space}\\p{Cc}]+/gu, \" \").replace(/^ | $/g, \"\");";
        var lowered = RegexPropertyEscapes.Lower(js);

        Assert.Equal(
            "return String(text || \"\").replace(/[\\u0009-\\u000D\\u0020\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028-\\u2029\\u202F\\u205F\\u3000\\u0000-\\u001F\\u007F-\\u009F]+/gu, \" \").replace(/^ | $/g, \"\");",
            lowered);
    }

    [Fact]
    public void A_negated_escape_outside_a_class_becomes_a_class_of_the_complement()
    {
        var lowered = RegexPropertyEscapes.Lower("x=/\\P{ASCII}/u");
        Assert.Equal("x=/[\\u0080-\\u{10FFFF}]/u", lowered.Replace("\\u{D800}-\\u{DFFF}", ""));
    }

    [Fact]
    public void A_negated_escape_inside_a_class_is_written_inline()
    {
        Assert.Equal("x=/[a\\u0000-\\u002F\\u003A-\\u0040\\u0047-\\u0060\\u0067-\\u{10FFFF}]/u",
            RegexPropertyEscapes.Lower("x=/[a\\P{AHex}]/u").Replace("\\u{D800}-\\u{DFFF}", ""));
    }

    [Fact]
    public void Astral_code_points_and_surrogates_are_written_in_braces()
    {
        var lowered = RegexPropertyEscapes.Lower("x=/\\p{L}/u");
        Assert.Contains("\\u{10000}", lowered);
        Assert.DoesNotContain("\\uD8", lowered);
    }

    [Theory]
    // No u flag: \p is just "p" there, nothing to rewrite.
    [InlineData("x=/\\p{L}/g")]
    // A property this pass does not know stays, and the whole regex with it.
    [InlineData("x=/[\\p{L}\\p{Script=Latin}]/u")]
    // An escaped backslash: the text is "\\p{L}", not an escape.
    [InlineData("x=/\\\\p{L}/u")]
    // In a string, not a regex.
    [InlineData("x=\"\\\\p{L}\"")]
    public void Leaves_alone_what_is_not_a_property_escape_it_knows(string js)
    {
        Assert.Same(js, RegexPropertyEscapes.Lower(js));
    }

    [Fact]
    public void A_division_before_the_regex_is_not_taken_for_its_start()
    {
        var lowered = RegexPropertyEscapes.Lower("y = a / b; z = /\\p{Nd}/u.test(s) / 2;");
        Assert.StartsWith("y = a / b; z = /[\\u0030-\\u0039", lowered);
        Assert.EndsWith("]/u.test(s) / 2;", lowered);
    }

    [Fact]
    public void A_slash_inside_a_class_does_not_end_the_regex()
    {
        var lowered = RegexPropertyEscapes.Lower("x=/[/]\\p{Nd}/u");
        Assert.StartsWith("x=/[/][\\u0030-\\u0039", lowered);
        Assert.EndsWith("]/u", lowered);
    }

    [Fact]
    public void Every_regex_on_a_line_is_rewritten()
    {
        var lowered = RegexPropertyEscapes.Lower("a=/\\p{Lu}/u;b=/\\p{Ll}/u;\nc=/\\p{Nd}/gu;");
        Assert.DoesNotContain("\\p{", lowered);
        Assert.Contains(";\nc=/[\\u0030-\\u0039", lowered);
    }
}
