using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Update
{
    /// <summary>
    /// Portable "is there a newer release?" check against a GitHub repo. Uses the Atom feed
    /// (no API rate limit) to find the latest release, compares versions, and optionally resolves
    /// a platform-specific download asset via the API. Downloading/applying the update is
    /// platform-specific and stays in each head (the desktop replaces its binary; mobile launches
    /// the Android package installer).
    /// </summary>
    public sealed class GitHubUpdateChecker
    {
        private readonly HttpClient _http;
        private readonly string _owner;
        private readonly string _repo;

        public GitHubUpdateChecker(HttpClient http, string repoOwner = "Apps2Samsung", string repoName = "Apps2Samsung")
        {
            _http = http;
            _owner = repoOwner;
            _repo = repoName;
        }

        public string ReleasesPageUrl => $"https://github.com/{_owner}/{_repo}/releases";
        private string AtomFeedUrl => $"https://github.com/{_owner}/{_repo}/releases.atom";
        private string LatestApiUrl => $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        // The specific release the feed identified — NOT /releases/latest, which GitHub always resolves
        // to the latest *stable* (so a detected beta would otherwise download the older stable asset).
        private string ReleaseByTagApiUrl(string tag) =>
            $"https://api.github.com/repos/{_owner}/{_repo}/releases/tags/{Uri.EscapeDataString(tag)}";

        /// <param name="currentVersion">The running app's version (e.g. "v2.7.0").</param>
        /// <param name="includePrereleases">
        /// false → only stable releases (desktop); true → include beta/pre-releases (mobile ships betas).
        /// </param>
        /// <param name="assetMatcher">Primary predicate to pick the download asset by file name.</param>
        /// <param name="assetFallbackMatcher">Secondary predicate if the primary matches nothing.</param>
        public async Task<UpdateCheckResult> CheckForUpdateAsync(
            string currentVersion,
            bool includePrereleases = false,
            Func<string, bool>? assetMatcher = null,
            Func<string, bool>? assetFallbackMatcher = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await CheckViaAtomFeedAsync(currentVersion, includePrereleases, cancellationToken);
                if (result.IsSuccess && result.IsUpdateAvailable && assetMatcher is not null)
                    await EnrichWithDownloadUrlAsync(result, assetMatcher, assetFallbackMatcher, cancellationToken);
                return result;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Update check failed: {ex}");
                return UpdateCheckResult.Failed($"Failed to check for updates: {ex.Message}", currentVersion);
            }
        }

        private async Task<UpdateCheckResult> CheckViaAtomFeedAsync(string currentVersion, bool includePrereleases, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, AtomFeedUrl);
                request.Headers.Accept.ParseAdd("application/atom+xml");
                request.Headers.UserAgent.ParseAdd("Apps2Samsung");

                using var response = await _http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var atomXml = await response.Content.ReadAsStringAsync(ct);
                var latestEntry = ParseAtomFeed(atomXml, includePrereleases);
                if (latestEntry == null)
                    return UpdateCheckResult.NoUpdateAvailable(currentVersion);

                var latestVersion = latestEntry.TagName;
                return new UpdateCheckResult
                {
                    IsUpdateAvailable = IsVersionGreater(latestVersion, currentVersion),
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ReleaseTitle = latestEntry.Title,
                    ReleaseNotes = StripHtml(RemoveMarkdownTable(latestEntry.Content)),
                    ReleasesPageUrl = latestEntry.Link,
                    PublishedAt = latestEntry.Updated,
                };
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Atom feed check failed: {ex}");
                return UpdateCheckResult.Failed($"Failed to parse release feed: {ex.Message}", currentVersion);
            }
        }

        private static GitHubAtomEntry? ParseAtomFeed(string atomXml, bool includePrereleases)
        {
            try
            {
                var doc = XDocument.Parse(atomXml);
                XNamespace atom = "http://www.w3.org/2005/Atom";

                // Stable-only unless prereleases are wanted (title carries "beta" for pre-releases).
                // Of what is left, take the highest version rather than the first entry: the feed is
                // ordered by date, and a re-published beta can sit above a newer stable.
                XElement? entry = null;
                var entryTag = string.Empty;
                foreach (var candidate in doc.Descendants(atom + "entry"))
                {
                    var title = candidate.Element(atom + "title")?.Value ?? string.Empty;
                    if (!includePrereleases && title.Contains("beta", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var tag = new GitHubAtomEntry
                    {
                        Id = candidate.Element(atom + "id")?.Value ?? string.Empty,
                        Link = candidate.Element(atom + "link")?.Attribute("href")?.Value ?? string.Empty,
                    }.TagName;
                    if (entry == null || IsVersionGreater(tag, entryTag))
                    {
                        entry = candidate;
                        entryTag = tag;
                    }
                }

                if (entry == null)
                    return null;

                return new GitHubAtomEntry
                {
                    Id = entry.Element(atom + "id")?.Value ?? string.Empty,
                    Title = entry.Element(atom + "title")?.Value ?? string.Empty,
                    Updated = DateTime.TryParse(entry.Element(atom + "updated")?.Value, out var updated) ? updated : null,
                    Link = entry.Element(atom + "link")?.Attribute("href")?.Value ?? string.Empty,
                    Content = entry.Element(atom + "content")?.Value ?? string.Empty,
                    AuthorName = entry.Element(atom + "author")?.Element(atom + "name")?.Value ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to parse Atom feed: {ex}");
                return null;
            }
        }

        private async Task EnrichWithDownloadUrlAsync(
            UpdateCheckResult result, Func<string, bool> match, Func<string, bool>? fallback, CancellationToken ct)
        {
            try
            {
                // Resolve the asset from the release the feed actually detected (may be a pre-release),
                // so the download matches the version we told the user about. Fall back to /releases/latest
                // only if we somehow don't have the tag.
                var apiUrl = string.IsNullOrWhiteSpace(result.LatestVersion)
                    ? LatestApiUrl
                    : ReleaseByTagApiUrl(result.LatestVersion);
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.UserAgent.ParseAdd("Apps2Samsung");
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    Trace.WriteLine($"GitHub API returned {response.StatusCode}, download URL unavailable");
                    return;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("assets", out var assets))
                    return;

                // Primary matcher first, then the fallback (if any).
                foreach (var predicate in new[] { match, fallback })
                {
                    if (predicate is null)
                        continue;
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("name", out var nameElement))
                            continue;
                        var name = nameElement.GetString() ?? string.Empty;
                        if (predicate(name) && asset.TryGetProperty("browser_download_url", out var urlElement))
                        {
                            result.DownloadUrl = urlElement.GetString();
                            if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size))
                                result.AssetSize = size;
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to get download URL: {ex}");
            }
        }

        /// <summary>
        /// True when <paramref name="latestVersion"/> is newer than <paramref name="currentVersion"/>.
        /// Tolerates a leading 'v'. A -suffix marks a pre-release and ranks below the release with the
        /// same number (semver), so v3.2.0 is an update for a running v3.2.0-beta, while v3.2.0-beta
        /// is not one for v3.2.0. Two pre-releases of the same number compare by their suffix.
        /// </summary>
        public static bool IsVersionGreater(string latestVersion, string currentVersion)
        {
            var (latestCore, latestSuffix) = SplitVersion(latestVersion);
            var (currentCore, currentSuffix) = SplitVersion(currentVersion);

            int coreOrder;
            if (Version.TryParse(latestCore, out var latest) && Version.TryParse(currentCore, out var current))
                coreOrder = Normalize(latest).CompareTo(Normalize(current));
            else
                coreOrder = string.Compare(latestCore, currentCore, StringComparison.OrdinalIgnoreCase);

            if (coreOrder != 0)
                return coreOrder > 0;

            // Same number: the release beats any pre-release of it.
            if (latestSuffix.Length == 0 || currentSuffix.Length == 0)
                return latestSuffix.Length == 0 && currentSuffix.Length > 0;
            return string.Compare(latestSuffix, currentSuffix, StringComparison.OrdinalIgnoreCase) > 0;
        }

        // "v3.2.0-beta" -> ("3.2.0", "beta"); "3.2.0" -> ("3.2.0", "").
        private static (string Core, string Suffix) SplitVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return ("0.0.0", string.Empty);

            var cleaned = version.Trim().TrimStart('v', 'V');
            // Build metadata (+sha) never orders versions.
            var plusIndex = cleaned.IndexOf('+');
            if (plusIndex >= 0)
                cleaned = cleaned.Substring(0, plusIndex);

            var dashIndex = cleaned.IndexOf('-');
            return dashIndex > 0
                ? (cleaned.Substring(0, dashIndex), cleaned.Substring(dashIndex + 1))
                : (cleaned, string.Empty);
        }

        // 3.2 and 3.2.0 are the same version; System.Version would call 3.2.0 the newer one.
        private static Version Normalize(Version v) =>
            new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        // Turns GitHub's HTML release-notes into readable plain text for a dialog/banner.
        private static string RemoveMarkdownTable(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;

            return Regex.Replace(html, @"(\|[^\n]+\|\s*\n)+", string.Empty, RegexOptions.Multiline);
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            var text = html
                .Replace("<br>", "\n")
                .Replace("<br/>", "\n")
                .Replace("<br />", "\n")
                .Replace("</p>", "\n")
                .Replace("</li>", "\n")
                .Replace("<li>", "• ");

            while (text.Contains('<') && text.Contains('>'))
            {
                var start = text.IndexOf('<');
                var end = text.IndexOf('>', start);
                if (end > start)
                    text = text.Remove(start, end - start + 1);
                else
                    break;
            }

            text = text
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'");

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)));
        }
    }
}
