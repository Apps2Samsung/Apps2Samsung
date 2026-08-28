using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Localization;
using System;
using System.Collections.Generic;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// The desktop head's <see cref="ILocalizationService"/>: a thin adapter over the shared
    /// <see cref="LocalizationCatalog"/>, which now owns the strings and the lookup so the mobile head
    /// can use them too. What stays here is the desktop-specific part — persisting the language in
    /// settings.json.
    /// </summary>
    public class LocalizationService : ILocalizationService
    {
        private readonly LocalizationCatalog _catalog = new();

        public LocalizationService()
        {
            var stored = AppSettings.Default.Language;
            _catalog.LanguageChanged += (_, _) => LanguageChanged?.Invoke(this, EventArgs.Empty);
            _catalog.SetLanguage(_catalog.ResolveInitialLanguage(stored));

            // First run (no stored language): commit the detected one so the settings dropdown reflects
            // it and it stays stable across launches (detect once, not "follow the OS forever").
            // Existing installs already have a value, so an explicit choice is never overridden.
            if (string.IsNullOrWhiteSpace(stored) && !string.IsNullOrWhiteSpace(_catalog.CurrentLanguage))
            {
                AppSettings.Default.Language = _catalog.CurrentLanguage;
                AppSettings.Default.Save();
            }
        }

        public string CurrentLanguage => _catalog.CurrentLanguage;

        public IEnumerable<string> AvailableLanguages => _catalog.AvailableLanguages;

        public event EventHandler? LanguageChanged;

        public string GetString(string key) => _catalog.GetString(key);

        public void SetLanguage(string languageCode) => _catalog.SetLanguage(languageCode);
    }
}
