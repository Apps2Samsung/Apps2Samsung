using System;
using System.Collections.Generic;

namespace Apps2Samsung.Interfaces
{
    public interface ILocalizationService
    {
        string GetString(string key);
        void SetLanguage(string languageCode);
        string CurrentLanguage { get; }
        IEnumerable<string> AvailableLanguages { get; }

        /// <summary>What a picker shows for a language, in that language's own words.</summary>
        string GetDisplayName(string languageCode);
        event EventHandler? LanguageChanged;
    }
}
