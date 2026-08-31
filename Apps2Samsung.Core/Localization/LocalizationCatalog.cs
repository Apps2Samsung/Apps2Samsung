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

        /// <summary>The language in use, as the locale that names its file (e.g. "pt-BR").</summary>
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
            return Match(storedLanguage)
                ?? Match(CultureInfo.CurrentUICulture.Name)
                ?? DefaultLanguage;
        }

        /// <summary>
        /// The closest language we have to <paramref name="code"/>, or null.
        /// <para>
        /// Widens the request one step at a time along the chain the framework itself defines -
        /// zh-Hans-CN, then zh-Hans, then zh - and takes the first available file that shares that
        /// ancestor. Script is handled by the chain rather than by naming it: a Simplified-Chinese
        /// device asks for zh-Hans-CN and zh-CN's own chain runs through zh-Hans, while zh-TW's runs
        /// through zh-Hant, so the two stay apart without either being mentioned here.
        /// </para>
        /// When a step matches more than one file - a stored bare "pt" against pt-BR and pt-PT - the
        /// tie goes to the region the framework considers that language's default, which is a real
        /// answer (pt to Brazil) rather than whichever sorted first.
        /// </summary>
        private string? Match(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            code = code!.Replace('_', '-').Trim();

            if (HasLanguage(code))
                return code;

            foreach (var ancestor in Chain(code))
            {
                var candidates = _languages.Keys
                    .Where(available => Chain(available).Contains(ancestor, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(available => available, StringComparer.Ordinal)
                    .ToList();

                if (candidates.Count == 0)
                    continue;
                if (candidates.Count == 1)
                    return candidates[0];

                var preferred = DefaultRegionOf(ancestor);
                return candidates.FirstOrDefault(
                           c => string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase))
                       ?? candidates[0];
            }

            return null;
        }

        /// <summary>
        /// A code and every broader form of it, most specific first: zh-Hans-CN, zh-Hans, zh. Uses the
        /// framework's own parent chain, which knows the script step, and falls back to cutting the
        /// code at its separators for anything it doesn't recognise.
        /// </summary>
        private static IEnumerable<string> Chain(string code)
        {
            var seen = new List<string>();

            try
            {
                for (var culture = new CultureInfo(code);
                     !string.IsNullOrEmpty(culture.Name);
                     culture = culture.Parent)
                {
                    seen.Add(culture.Name);
                }
            }
            catch (CultureNotFoundException)
            {
                // Crowdin ships a handful of codes the framework has never heard of (sr-SP names no
                // real region), so widen them by hand rather than losing the language.
                seen.Clear();
                for (var cut = code.Length; cut > 0; cut = code.LastIndexOf('-', cut - 1))
                {
                    seen.Add(code[..cut]);
                    if (code.LastIndexOf('-', cut - 1) < 0)
                        break;
                }
            }

            return seen;
        }

        /// <summary>The region the framework treats as a language's default - "pt" gives pt-BR.</summary>
        private static string? DefaultRegionOf(string language)
        {
            try
            {
                return CultureInfo.CreateSpecificCulture(language).Name;
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// What a language picker should show for <paramref name="code"/>, in that language's own
        /// words. Names the region only when it distinguishes something: with one Dutch file this is
        /// "Nederlands", and the day a nl-BE file arrives both become "Nederlands (Nederland)" and
        /// "Nederlands (Belgie)" on their own. That keeps the list readable without a table of
        /// hand-written names to maintain - and it is why the files are named by full locale, since
        /// pt-BR.json carries the region that pt.json could only imply.
        /// </summary>
        public string GetDisplayName(string code)
        {
            try
            {
                var culture = new CultureInfo(code);
                var language = culture.TwoLetterISOLanguageName;

                var variants = _languages.Keys.Count(available => IsLanguage(available, language));
                var name = variants > 1
                    ? culture.NativeName
                    : new CultureInfo(language).NativeName;

                return string.IsNullOrEmpty(name) ? code : char.ToUpper(name[0]) + name[1..];
            }
            catch (CultureNotFoundException)
            {
                return code;
            }
        }

        private static bool IsLanguage(string code, string language)
        {
            try
            {
                return string.Equals(new CultureInfo(code).TwoLetterISOLanguageName, language,
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch (CultureNotFoundException)
            {
                return false;
            }
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
