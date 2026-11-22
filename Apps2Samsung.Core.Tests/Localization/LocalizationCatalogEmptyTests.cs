using Apps2Samsung.Localization;

namespace Apps2Samsung.Tests.Localization;

// Crowdin exports a translation that was saved empty as "" (three Arabic strings, 2026-11-22). The
// app showed that as a blank line; it is no translation and falls through to English.
public class LocalizationCatalogEmptyTests
{
    [Fact]
    public void An_empty_translation_shows_the_english_text()
    {
        var catalog = new LocalizationCatalog();
        var english = catalog.GetString("lblRefresh");
        catalog.AddLanguage("xx-TEST", new Dictionary<string, string>
        {
            ["lblRefresh"] = "",
            ["lblCancel"] = "   ",
            ["lblInstall"] = "Translated",
        });

        catalog.SetLanguage("xx-TEST");

        Assert.Equal(english, catalog.GetString("lblRefresh"));
        Assert.NotEqual("   ", catalog.GetString("lblCancel"));
        Assert.Equal("Translated", catalog.GetString("lblInstall"));
    }
}
