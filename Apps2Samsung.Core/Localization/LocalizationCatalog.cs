using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Apps2Samsung.Localization
{
    /// <summary>
    /// The translated strings, and the lookup over them, for both heads. Previously this lived in the
    /// desktop head, which is why the mobile head shipped English-only and why Core code has had to
    /// carry a localization key *and* an English default string for anything it wanted to say.
    /// <para>
    /// The language files themselves stay at <c>Jellyfin2Samsung-CrossOS/Assets/Localization/</c>
    /// because that is the path Crowdin syncs (see crowdin.yml) — moving them would orphan the
    /// existing translations. Core embeds them from there instead (see Apps2Samsung.Core.csproj), so
    /// there is one copy at build time and no per-head asset plumbing: a MAUI head can't read an
    /// Avalonia <c>avares://</c> resource, and neither head has to know where the files came from.
    /// </para>
    /// Lookup order for a key: the current language, then English, then the key itself — so a string a
    /// translator hasn't reached yet shows in English rather than blank.
    /// </summary>
    public sealed class LocalizationCatalog
    {
        public const string DefaultLanguage = "en";

        private const string ResourcePrefix = "Apps2Samsung.Core.Localization.";

        private readonly Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _current = new();

        public LocalizationCatalog()
        {
            LoadEmbeddedLanguages();
        }

        /// <summary>The language in use, as a two-letter code.</summary>
        public string CurrentLanguage { get; private set; } = DefaultLanguage;

        /// <summary>Every language that has a file, sorted — what a language picker binds to.</summary>
        public IEnumerable<string> AvailableLanguages => _languages.Keys.OrderBy(code => code, StringComparer.Ordinal);

        /// <summary>Raised after <see cref="SetLanguage"/> changes the language, for UI that must re-read strings.</summary>
        public event EventHandler? LanguageChanged;

        /// <summary>True when a file for this language was loaded.</summary>
        public bool HasLanguage(string languageCode) => _languages.ContainsKey(languageCode);

        /// <summary>
        /// The translated string for <paramref name="key"/>, falling back to English and finally to the
        /// key itself (which makes a missing key visible in the UI rather than silently blank).
        /// </summary>
        public string GetString(string key)
        {
            if (_current.TryGetValue(key, out var value))
                return value;

            if (_languages.TryGetValue(DefaultLanguage, out var english) &&
                english.TryGetValue(key, out var englishValue))
                return englishValue;

            return key;
        }

        /// <summary>
        /// Switches language. An unknown code falls back to English rather than leaving the UI empty.
        /// </summary>
        public void SetLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode) || !_languages.TryGetValue(languageCode, out var strings))
            {
                languageCode = DefaultLanguage;
                strings = _languages.GetValueOrDefault(DefaultLanguage, new Dictionary<string, string>());
            }

            CurrentLanguage = languageCode;
            _current = strings;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Picks the language to start in: the user's stored choice when it is one we have, otherwise
        /// the OS display language, otherwise English. Detection happens once — each head persists the
        /// result — so the app doesn't silently follow the OS after the user has chosen.
        /// </summary>
        public string ResolveInitialLanguage(string? storedLanguage)
        {
            if (!string.IsNullOrWhiteSpace(storedLanguage) && HasLanguage(storedLanguage!))
                return storedLanguage!;

            var systemLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return HasLanguage(systemLanguage) ? systemLanguage : DefaultLanguage;
        }

        private void LoadEmbeddedLanguages()
        {
            var assembly = typeof(LocalizationCatalog).Assembly;

            foreach (var resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var code = resource[ResourcePrefix.Length..^".json".Length];
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                TryLoad(assembly, resource, code);
            }

            if (_languages.Count == 0)
                Trace.WriteLine("[i18n] no language resources found — every string will fall back to its key.");
        }

        private void TryLoad(Assembly assembly, string resourceName, string languageCode)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    return;

                using var reader = new StreamReader(stream);
                var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
                if (strings is not null)
                    _languages[languageCode] = strings;
            }
            catch (Exception ex)
            {
                // One malformed translation file shouldn't cost the app every other language.
                Trace.WriteLine($"[i18n] could not load '{languageCode}': {ex.Message}");
            }
        }
    }
}
