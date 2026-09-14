using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// One app in the community catalogue: the name a person recognises, the Tizen id(s) the TV knows
    /// it by (the upstream file lists several for apps whose id changed between firmware generations),
    /// and its store icon.
    /// </summary>
    public sealed record SamsungTvApp(string Name, IReadOnlyList<string> Ids, string IconUrl)
    {
        /// <summary>The id to launch with — the first one listed, which is the current one upstream.</summary>
        public string Id => Ids.Count > 0 ? Ids[0] : string.Empty;
    }

    /// <summary>
    /// The community-maintained Samsung TV app-ID catalogue (name → Tizen id → icon), published by
    /// <c>Apps2Samsung/tizen-community-packages</c>. Two readers share it: the installed-apps list,
    /// which wants the icons (<see cref="AppIconResolver"/>), and the remote's TV toolbox, which wants
    /// the ids so it can launch an app the TV's own launcher hides (#635).
    /// <para>
    /// Sources in order: our mirror in the Apps2Samsung org, then the upstream community file it was
    /// mirrored from, then the copy embedded in this assembly. The embedded copy is what a phone with
    /// no network gets — a stale catalogue still launches Netflix, and app ids barely move.
    /// </para>
    /// Read at most once per run: the file is ~7 KB and nothing in a session invalidates it.
    /// </summary>
    public static class SamsungTvAppCatalog
    {
        // Short timeout: the callers are UI paths, and the embedded copy is right there.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

        private static readonly string[] Sources =
        {
            "https://raw.githubusercontent.com/Apps2Samsung/tizen-community-packages/refs/heads/main/data/samsung-tv-app-ids.json",
            "https://rs.ltd/data/samsung-tv-app-ids.json",
        };

        private const string EmbeddedResourceName = "Apps2Samsung.Core.Catalog.samsung-tv-app-ids.json";

        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static IReadOnlyList<SamsungTvApp>? _cache;

        /// <summary>True when the catalogue in hand is the embedded copy rather than a fresh fetch.</summary>
        public static bool IsOffline { get; private set; }

        /// <summary>
        /// The catalogue, sorted by name. Never throws, and only comes back empty if even the embedded
        /// copy can't be read.
        /// </summary>
        public static async Task<IReadOnlyList<SamsungTvApp>> GetAsync(CancellationToken cancellationToken = default)
        {
            var cached = _cache;
            if (cached is not null)
                return cached;

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // A second caller that queued behind the first fetch takes its result.
                if (_cache is not null)
                    return _cache;

                foreach (var url in Sources)
                {
                    var fetched = await TryFetchAsync(url, cancellationToken).ConfigureAwait(false);
                    if (fetched is null)
                        continue;

                    IsOffline = false;
                    _cache = fetched;
                    return fetched;
                }

                IsOffline = true;
                _cache = ReadEmbedded();
                return _cache;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<IReadOnlyList<SamsungTvApp>?> TryFetchAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var apps = Parse(json);
                // A 200 that parses to nothing (a captive portal's login page, say) is not a catalogue.
                return apps.Count > 0 ? apps : null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[catalog] could not read the app-ID catalogue from {url}: {ex.Message}");
                return null;
            }
        }

        private static IReadOnlyList<SamsungTvApp> ReadEmbedded()
        {
            try
            {
                using var stream = typeof(SamsungTvAppCatalog).Assembly.GetManifestResourceStream(EmbeddedResourceName);
                if (stream is null)
                {
                    Trace.WriteLine($"[catalog] embedded catalogue {EmbeddedResourceName} is missing from the assembly.");
                    return Array.Empty<SamsungTvApp>();
                }

                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[catalog] could not read the embedded app-ID catalogue: {ex.Message}");
                return Array.Empty<SamsungTvApp>();
            }
        }

        private static IReadOnlyList<SamsungTvApp> Parse(string json)
        {
            TvAppIconManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<TvAppIconManifest>(json, JsonSerializerOptionsProvider.Default);
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"[catalog] app-ID catalogue is not valid JSON: {ex.Message}");
                return Array.Empty<SamsungTvApp>();
            }

            if (manifest?.Items is null)
                return Array.Empty<SamsungTvApp>();

            var apps = new List<SamsungTvApp>(manifest.Items.Count);
            foreach (var item in manifest.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                    continue;

                // The id field carries a comma-separated list where an app has more than one id.
                var ids = item.Id.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ids.Length == 0)
                    continue;

                apps.Add(new SamsungTvApp(
                    Name: string.IsNullOrWhiteSpace(item.Name) ? ids[0] : item.Name.Trim(),
                    Ids: new ReadOnlyCollection<string>(ids),
                    IconUrl: item.Icon ?? string.Empty));
            }

            return new ReadOnlyCollection<SamsungTvApp>(
                apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }
    }
}
