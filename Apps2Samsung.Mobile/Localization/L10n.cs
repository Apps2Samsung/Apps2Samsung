using Apps2Samsung.Localization;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Localization;

/// <summary>
/// The mobile head's access to the shared <see cref="LocalizationCatalog"/> — the same strings and the
/// same 28 languages the desktop head shows. This head was English-only until the catalog moved into
/// Core; all that is needed here is the store for the chosen language (MAUI Preferences) and a static
/// entry point the pages and the <see cref="LocalizeExtension"/> can reach without DI.
/// </summary>
public static class L10n
{
    private static LocalizationCatalog? _catalog;

    /// <summary>The catalog, created and pointed at the right language on first use.</summary>
    public static LocalizationCatalog Catalog
    {
        get
        {
            if (_catalog is not null)
                return _catalog;

            var catalog = new LocalizationCatalog();
            var stored = MobileSettings.Language;
            catalog.SetLanguage(catalog.ResolveInitialLanguage(stored));

            // First run: commit the detected language so it stays put once chosen, matching the
            // desktop head (detect once, don't silently follow the OS afterwards).
            if (string.IsNullOrWhiteSpace(stored))
                MobileSettings.Language = catalog.CurrentLanguage;

            _catalog = catalog;
            return _catalog;
        }
    }

    /// <summary>The translated string for <paramref name="key"/>; falls back to English, then the key.</summary>
    public static string Get(string key) => Catalog.GetString(key);

    /// <summary>Every language with a translation file, for the picker in Settings.</summary>
    public static IEnumerable<string> AvailableLanguages => Catalog.AvailableLanguages;

    /// <summary>What a picker shows for a language, in that language's own words.</summary>
    public static string GetDisplayName(string languageCode) => Catalog.GetDisplayName(languageCode);

    /// <summary>The language in use.</summary>
    public static string CurrentLanguage => Catalog.CurrentLanguage;

    /// <summary>
    /// Switches language and remembers it. MAUI resolves <c>{l:Localize}</c> once per page build, so
    /// the caller reloads the UI (the Settings page asks the user to reopen the app) rather than this
    /// trying to re-translate a live view tree.
    /// </summary>
    public static void SetLanguage(string languageCode)
    {
        Catalog.SetLanguage(languageCode);
        MobileSettings.Language = Catalog.CurrentLanguage;
    }
}
