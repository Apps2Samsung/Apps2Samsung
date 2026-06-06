using Avalonia.Platform;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Apps2Samsung.Services
{
    public class LocalizationService : ILocalizationService
    {
        private const string DefaultLanguage = "en";
        private const string LocalizationFolderUri = "avares://Apps2Samsung/Assets/Localization/";

        private Dictionary<string, string> _currentStrings = new();
        private readonly Dictionary<string, Dictionary<string, string>> _allStrings = new();
        private string _currentLanguage = DefaultLanguage;

        public string CurrentLanguage => _currentLanguage;
        public IEnumerable<string> AvailableLanguages => _allStrings.Keys.OrderBy(x => x);
        public event EventHandler? LanguageChanged;

        public LocalizationService()
        {
            LoadLanguages();
        }

        private void LoadLanguages()
        {
            _allStrings.Clear();

            var folderUri = new Uri(LocalizationFolderUri);

            try
            {
                var assetUris = AssetLoader.GetAssets(folderUri, null);

                foreach (var assetUri in assetUris)
                {
                    if (!assetUri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fileName = Path.GetFileNameWithoutExtension(assetUri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        continue;

                    TryLoadLanguage(fileName);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to enumerate localization assets: {ex}");
            }

            // Always make sure English is attempted as fallback language
            if (!_allStrings.ContainsKey(DefaultLanguage))
            {
                TryLoadLanguage(DefaultLanguage);
            }

            var systemLang = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
            var configLang = AppSettings.Default.Language;

            var initialLang =
                !string.IsNullOrWhiteSpace(configLang) && _allStrings.ContainsKey(configLang)
                    ? configLang
                    : _allStrings.ContainsKey(systemLang)
                        ? systemLang
                        : DefaultLanguage;

            SetLanguage(initialLang);
        }

        private void TryLoadLanguage(string lang)
        {
            try
            {
                var uri = new Uri($"{LocalizationFolderUri}{lang}.json");

                using var asset = AssetLoader.Open(uri);
                using var reader = new StreamReader(asset);
                var json = reader.ReadToEnd();

                var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (strings != null)
                {
                    _allStrings[lang] = strings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to load language {lang}: {ex}");
            }
        }

        public string GetString(string key)
        {
            if (_currentStrings.TryGetValue(key, out var value))
                return value;

            if (_allStrings.TryGetValue(DefaultLanguage, out var englishStrings) &&
                englishStrings.TryGetValue(key, out var englishValue))
                return englishValue;

            return key;
        }

        public void SetLanguage(string languageCode)
        {
            if (!_allStrings.TryGetValue(languageCode, out var strings))
            {
                languageCode = DefaultLanguage;
                strings = _allStrings.GetValueOrDefault(DefaultLanguage, new Dictionary<string, string>());
            }

            _currentLanguage = languageCode;
            _currentStrings = strings;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}