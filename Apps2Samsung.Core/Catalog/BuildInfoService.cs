using System.Text.Json;
using System.Text.RegularExpressions;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>One row for the catalog/build-info view: an app name, blurb, version, and preview URL.</summary>
    public sealed class BuildInfoItem
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string? PreviewImageUrl { get; init; }
        /// <summary>True for a community-package row, false for a Jellyfin build/fork.</summary>
        public bool IsCommunityApp { get; init; }
    }

    /// <summary>The assembled catalog view: Jellyfin builds/forks and community apps.</summary>
    public sealed record BuildInfoResult(
        IReadOnlyList<BuildInfoItem> JellyfinBuilds,
        IReadOnlyList<BuildInfoItem> CommunityApps);

    /// <summary>
    /// Shared "app preview + versions" helper (the desktop's "?" catalog window). Parses the
    /// upstream README markdown tables, resolves preview images from the provider manifest, and
    /// fetches the latest release tag per source. UI-free — each head renders the result (loads the
    /// preview bitmaps, builds its own view).
    /// </summary>
    public sealed class BuildInfoService
    {
        // Upstream READMEs the desktop reads today (kept as defaults; callers may override).
        public const string DefaultJellyfinReadmeUrl =
            "https://raw.githubusercontent.com/jeppevinkel/jellyfin-tizen-builds/refs/heads/master/README.md";
        public const string DefaultCommunityReadmeUrl =
            "https://raw.githubusercontent.com/Apps2Samsung/tizen-community-packages/refs/heads/main/README.md";

        private static readonly Regex VersionsTable =
            new(@"## Versions\s*\n(?<table>(\|[^\n]+\n)+)", RegexOptions.Compiled);
        private static readonly Regex TableRow2Columns =
            new(@"^\|([^|]+)\|([^|]+)\|", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex MarkdownBold = new(@"\*\*(.*?)\*\*", RegexOptions.Compiled);
        private static readonly Regex EmojiRange = new(@"[☀-➿]", RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly Func<string?>? _tokenProvider;

        public BuildInfoService(HttpClient http, Func<string?>? tokenProvider = null)
        {
            _http = http;
            _tokenProvider = tokenProvider;
        }

        /// <summary>
        /// Full assembly for the catalog view: parses both READMEs, folds in the manifest's
        /// provider build-info + preview images, and stamps each row with its latest release tag.
        /// </summary>
        public async Task<BuildInfoResult> LoadAsync(
            ProviderManifest manifest,
            string? jellyfinReadmeUrl = null,
            string? communityReadmeUrl = null)
        {
            var jellyfinMd = await GetStringOrEmptyAsync(jellyfinReadmeUrl ?? DefaultJellyfinReadmeUrl);
            var communityMd = await GetStringOrEmptyAsync(communityReadmeUrl ?? DefaultCommunityReadmeUrl);

            var jellyfinPreview = manifest.PreviewImages?.Jellyfin;
            var providerOverrides = manifest.Providers
                .Where(p => p.BuildInfo is { } bi && !string.IsNullOrWhiteSpace(bi.Name) && !string.IsNullOrWhiteSpace(bi.PreviewImage))
                .ToDictionary(p => p.BuildInfo!.Name, p => p.BuildInfo!.PreviewImage!, StringComparer.OrdinalIgnoreCase);
            var communityPreviews = manifest.CommunityApps
                .Where(c => !string.IsNullOrWhiteSpace(c.MatchName) && !string.IsNullOrWhiteSpace(c.PreviewImage))
                .ToDictionary(c => c.MatchName, c => c.PreviewImage, StringComparer.OrdinalIgnoreCase);

            // Jellyfin core builds share one upstream release; fetch its tag once.
            var coreUrl = manifest.Providers
                .FirstOrDefault(p => p.Url?.Contains("jellyfin-tizen-builds", StringComparison.OrdinalIgnoreCase) == true)
                ?.Url ?? string.Empty;
            var coreTag = string.IsNullOrWhiteSpace(coreUrl) ? string.Empty : await GetLatestReleaseTagAsync(coreUrl);

            var jellyfin = new List<BuildInfoItem>();
            foreach (var parsed in ParseVersionsTable(jellyfinMd))
                jellyfin.Add(new BuildInfoItem
                {
                    Name = parsed.Name,
                    Description = parsed.Description,
                    Version = coreTag,
                    PreviewImageUrl = ResolvePreview(parsed.Name, providerOverrides) ?? jellyfinPreview,
                });

            // Provider forks (Moonfin, Litefin, AVPlay, …) — each with its own release tag + preview.
            foreach (var provider in manifest.Providers)
            {
                if (provider.BuildInfo is not { } bi || string.IsNullOrWhiteSpace(bi.Name))
                    continue;

                var tag = string.IsNullOrWhiteSpace(provider.Url) ? string.Empty : await GetLatestReleaseTagAsync(provider.Url);
                jellyfin.Add(new BuildInfoItem
                {
                    Name = bi.Name,
                    Description = bi.Description,
                    Version = tag,
                    PreviewImageUrl = string.IsNullOrWhiteSpace(bi.PreviewImage) ? jellyfinPreview : bi.PreviewImage,
                });
            }

            var community = ParseApplicationsTable(communityMd)
                .Select(p => new BuildInfoItem
                {
                    Name = p.Name,
                    Description = p.Description,
                    Version = p.Version,
                    PreviewImageUrl = ResolvePreview(p.Name, communityPreviews),
                    IsCommunityApp = true,
                })
                .ToList();

            jellyfin.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            community.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new BuildInfoResult(jellyfin, community);
        }

        /// <summary>Fetches the latest release tag for a /releases or /releases/tags/&lt;tag&gt; URL.</summary>
        public async Task<string> GetLatestReleaseTagAsync(string url)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Apps2Samsung");
                var token = _tokenProvider?.Invoke();
                if (!string.IsNullOrWhiteSpace(token) && new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                using var response = await _http.SendAsync(request);
                var json = (await response.Content.ReadAsStringAsync()).TrimStart();

                if (json.StartsWith("["))
                {
                    var list = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonSerializerOptionsProvider.Default);
                    var first = list?.FirstOrDefault();
                    return first?.TagName ?? first?.Name ?? string.Empty;
                }

                var single = JsonSerializer.Deserialize<GitHubRelease>(json, JsonSerializerOptionsProvider.Default);
                return single?.TagName ?? single?.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // ---- README markdown parsing (shared by both heads) ----

        /// <summary>Parses the "## Versions" 2-column (file name, description) table.</summary>
        public static IReadOnlyList<BuildInfoItem> ParseVersionsTable(string markdown)
        {
            var items = new List<BuildInfoItem>();
            var match = VersionsTable.Match(markdown);
            if (!match.Success)
                return items;

            var table = match.Groups["table"].Value;
            var headerSkipped = false;

            foreach (Match row in TableRow2Columns.Matches(table))
            {
                var col1 = row.Groups[1].Value.Trim();
                var col2 = row.Groups[2].Value.Trim();

                if (!headerSkipped && col1.Equals("File name", StringComparison.OrdinalIgnoreCase))
                {
                    headerSkipped = true;
                    continue;
                }
                if (col1.StartsWith("-"))
                    continue;

                items.Add(new BuildInfoItem { Name = CleanText(col1), Description = CleanText(col2) });
            }
            return items;
        }

        /// <summary>
        /// Parses the community "Applications" table. Header-driven, so the README's column
        /// order/count can change (e.g. adding a Version column) without breaking parsing.
        /// </summary>
        public static IReadOnlyList<BuildInfoItem> ParseApplicationsTable(string markdown)
        {
            var items = new List<BuildInfoItem>();
            var lines = markdown.Replace("\r\n", "\n").Split('\n');

            int headerIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i].TrimStart();
                if (l.StartsWith("|")
                    && l.Contains("Application", StringComparison.OrdinalIgnoreCase)
                    && l.Contains("Description", StringComparison.OrdinalIgnoreCase))
                {
                    headerIdx = i;
                    break;
                }
            }
            if (headerIdx < 0)
                return items;

            var headers = SplitTableRow(lines[headerIdx]);
            int nameCol = FindColumn(headers, "Application");
            int descCol = FindColumn(headers, "Description");
            int verCol = FindColumn(headers, "Version");
            if (nameCol < 0 || descCol < 0)
                return items;

            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("|"))
                    break; // table ended

                var cells = SplitTableRow(lines[i]);
                if (cells.Count == 0)
                    continue;
                if (cells.All(c => c.Trim().Trim('-', ':', ' ').Length == 0))
                    continue; // separator row

                var name = nameCol < cells.Count ? CleanText(cells[nameCol]) : string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                items.Add(new BuildInfoItem
                {
                    Name = name,
                    Description = descCol < cells.Count ? CleanText(cells[descCol]) : string.Empty,
                    Version = verCol >= 0 && verCol < cells.Count ? CleanText(cells[verCol]).Trim('`', ' ') : string.Empty,
                    IsCommunityApp = true,
                });
            }
            return items;
        }

        private static string? ResolvePreview(string name, Dictionary<string, string> byMatchName)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            foreach (var kvp in byMatchName)
                if (name.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            return null;
        }

        private static string CleanText(string input)
        {
            var text = MarkdownBold.Replace(input, "$1");
            text = EmojiRange.Replace(text, "");
            return text.Trim();
        }

        private static List<string> SplitTableRow(string row)
        {
            row = row.Trim();
            if (row.StartsWith("|")) row = row[1..];
            if (row.EndsWith("|")) row = row[..^1];
            return row.Split('|').ToList();
        }

        private static int FindColumn(List<string> headers, string name)
        {
            for (int i = 0; i < headers.Count; i++)
                if (headers[i].Contains(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private async Task<string> GetStringOrEmptyAsync(string url)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Apps2Samsung");
                using var response = await _http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
