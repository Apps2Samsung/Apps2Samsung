using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// A source the user added themselves (Settings > Installing > Your sources, the catalog's ⋯ >
    /// Add a source, <c>apps2samsung-cli sources add</c>): a GitHub repository or one of its releases,
    /// whose .wgt/.tpk assets are listed like any provider's, a direct link to one such file, or a
    /// feed: a .json in the installer's own <c>third-party-apps.json</c> shape listing several
    /// providers at once. For a developer that is the way to install a build before, or without, it
    /// being in tizen-community-packages. Stored as JSON in the head's settings, read on every catalog
    /// load, so Refresh and a restart pick up a new release.
    /// </summary>
    public sealed class UserSource
    {
        /// <summary>What the list shows; blank takes <see cref="UserSources.DefaultName"/>.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>As typed: a github.com page, owner/repo, an api.github.com URL or a link to a file.</summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>Category id (<see cref="AppCategories"/>) the entries are filed under; unknown lands in "other".</summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = AppCategories.Other;

        /// <summary>True when the app needs Partner signing (restricted privileges).</summary>
        [JsonPropertyName("partner")]
        public bool Partner { get; set; }

        // ---- a signed repository (#1018): set only once the user approved its key ----

        /// <summary>The repository's <c>repository.id</c>, pinned: a manifest naming another is rejected.</summary>
        [JsonPropertyName("repositoryId")]
        public string? RepositoryId { get; set; }

        /// <summary>The approved key's fingerprint (SHA-256 of its DER SubjectPublicKeyInfo, lowercase hex).</summary>
        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; set; }

        /// <summary>The approved key itself (PEM). Every load verifies against this one, never a newly fetched key.</summary>
        [JsonPropertyName("publicKey")]
        public string? PublicKey { get; set; }

        /// <summary>The <c>generatedAt</c> accepted when the key was approved; a manifest older than this is a rollback.</summary>
        [JsonPropertyName("acceptedGeneratedAt")]
        public DateTimeOffset? AcceptedGeneratedAt { get; set; }
    }

    public enum UserSourceKind
    {
        /// <summary>Not something the installer can read.</summary>
        None,
        /// <summary>A GitHub repository: its newest release with installable assets.</summary>
        GitHubReleases,
        /// <summary>One GitHub release, by tag.</summary>
        GitHubReleaseTag,
        /// <summary>A link straight to a .wgt or .tpk.</summary>
        DirectFile,
        /// <summary>
        /// A .json in the <see cref="ProviderManifest"/> shape (or a bare array of its providers): every
        /// provider in it is listed, so one link can publish a whole set of apps.
        /// </summary>
        Feed,
        /// <summary>
        /// An independent signed repository (#1018): an HTTPS link to its <c>repository.json.signed</c>,
        /// checked against the key the user approved (<see cref="Repositories.SignedRepositories"/>).
        /// </summary>
        SignedRepository,
    }

    public static class UserSources
    {
        /// <summary>
        /// Every provider made from a user source gets an id with this prefix, so the pre-resolved
        /// snapshot and the download counts (both keyed by provider id) never match one.
        /// </summary>
        public const string IdPrefix = "user-";

        /// <summary>Field keys <see cref="Validate"/> reports on, as the forms name their boxes.</summary>
        public const string FieldUrl = "url";
        public const string FieldName = "name";

        private static readonly Regex SlugPattern = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };

        /// <summary>The stored list; empty for blank or unreadable JSON, never throws.</summary>
        public static List<UserSource> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<UserSource>();
            try
            {
                var list = JsonSerializer.Deserialize<List<UserSource>>(json, JsonOptions) ?? new List<UserSource>();
                return list.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Url)).ToList();
            }
            catch (JsonException)
            {
                return new List<UserSource>();
            }
        }

        /// <summary>The list as the settings store it; empty string for no sources.</summary>
        public static string Serialize(IReadOnlyCollection<UserSource> sources)
            => sources.Count == 0 ? string.Empty : JsonSerializer.Serialize(sources, JsonOptions);

        /// <summary>What kind of source <paramref name="url"/> is, after <see cref="Normalize"/>.</summary>
        public static UserSourceKind Classify(string? url) => Normalize(url).Kind;

        public static bool IsDirectFile(string? url) => Classify(url) == UserSourceKind.DirectFile;

        public static bool IsFeed(string? url) => Classify(url) == UserSourceKind.Feed;

        public static bool IsSignedRepository(string? url) => Classify(url) == UserSourceKind.SignedRepository;

        /// <summary>A signed repository whose key the user approved: the only kind that is not labelled Unverified.</summary>
        public static bool IsApprovedRepository(UserSource source) =>
            IsSignedRepository(source.Url) && !string.IsNullOrWhiteSpace(source.PublicKey)
            && !string.IsNullOrWhiteSpace(source.Fingerprint) && !string.IsNullOrWhiteSpace(source.RepositoryId);

        /// <summary>
        /// The URL a provider fetches: the GitHub API releases URL for a repository or release page,
        /// the file URL itself for a direct link; null for anything the installer can't read.
        /// </summary>
        public static string? ApiUrl(string? url) => Normalize(url).ApiUrl;

        /// <summary>The file name a direct link points at ("MyApp.wgt"); empty for any other URL.</summary>
        public static string FileName(string? url)
        {
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
                return string.Empty;
            var name = Uri.UnescapeDataString(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty);
            return IsPackageName(name) ? name : string.Empty;
        }

        /// <summary>The name a source lists under when the user gave none: the repository, the file, or the feed's host.</summary>
        public static string DefaultName(string? url)
        {
            var (kind, api, slug) = Normalize(url);
            return kind switch
            {
                UserSourceKind.DirectFile => Path.GetFileNameWithoutExtension(FileName(url)),
                UserSourceKind.Feed or UserSourceKind.SignedRepository => Uri.TryCreate(api, UriKind.Absolute, out var feed) ? feed.Host : api ?? string.Empty,
                UserSourceKind.None => url?.Trim() ?? string.Empty,
                _ => slug?.Split('/').Last() ?? api ?? string.Empty,
            };
        }

        /// <summary>The name shown for <paramref name="source"/>: its own, else <see cref="DefaultName"/>.</summary>
        public static string DisplayName(UserSource source)
            => string.IsNullOrWhiteSpace(source.Name) ? DefaultName(source.Url) : source.Name.Trim();

        /// <summary>
        /// The provider id for <paramref name="source"/>: <see cref="IdPrefix"/> plus a hash of the
        /// fetched URL, so the same source gets the same id on every head and run.
        /// </summary>
        public static string Id(UserSource source)
        {
            var key = (ApiUrl(source.Url) ?? source.Url.Trim()).ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return IdPrefix + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        /// <summary>
        /// What is wrong with <paramref name="source"/>, by field (<see cref="FieldUrl"/>) to the
        /// en.json key of the message; empty when it can be added. A URL that fetches the same thing
        /// as one in <paramref name="existing"/> is a duplicate.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Validate(UserSource source, IEnumerable<UserSource>? existing = null)
        {
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            var api = ApiUrl(source.Url);
            if (api is null)
                errors[FieldUrl] = "errUserSourceUrl";
            else if (existing is not null && existing.Any(e => !ReferenceEquals(e, source)
                         && string.Equals(ApiUrl(e.Url), api, StringComparison.OrdinalIgnoreCase)))
                errors[FieldUrl] = "errUserSourceDuplicate";
            if (source.Name.Contains('\n'))
                errors[FieldName] = "errContributeOneLine";
            return errors;
        }

        /// <summary>
        /// <paramref name="source"/> as a manifest provider, so the heads list it through the same
        /// loop as the built-in ones. Null when its URL is not something the installer can read, and
        /// null for a feed, which is several providers (<see cref="FeedProvidersAsync"/>).
        /// </summary>
        public static ProviderEntry? ToProvider(UserSource source)
        {
            var api = ApiUrl(source.Url);
            if (api is null || IsFeed(source.Url) || IsSignedRepository(source.Url))
                return null;
            return new ProviderEntry
            {
                Id = Id(source),
                Url = api,
                DisplayName = DisplayName(source),
                Take = 1,
                Category = AppCategories.Normalize(source.Category),
                CertLevel = source.Partner ? "partner" : string.Empty,
                IsUserSource = true,
                Source = new CatalogSource(CatalogSourceKind.Unverified, DisplayName(source)),
            };
        }

        /// <summary>
        /// The manifest's providers followed by the user's, skipping a user source that fetches a URL
        /// the manifest already lists (the app would appear twice) and a second copy of the same
        /// user source. Feeds are left out here: <see cref="ResolveAsync"/> reads them.
        /// </summary>
        public static List<ProviderEntry> Merge(IEnumerable<ProviderEntry> manifestProviders, IEnumerable<UserSource> sources)
        {
            var result = manifestProviders.ToList();
            var urls = new HashSet<string>(result.Select(p => p.Url ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
                AddProvider(result, urls, ToProvider(source));
            return result;
        }

        /// <summary>
        /// <see cref="Merge"/>, then every feed among <paramref name="sources"/> read and its providers
        /// appended (<see cref="FeedProvidersAsync"/>). A feed that cannot be read or parsed is
        /// skipped and traced; the rest of the catalog is unaffected. This is what the heads call on
        /// every catalog load.
        /// </summary>
        /// <param name="repositories">
        /// Where signed repositories (#1018) keep their last verified copy; null leaves them out of the
        /// catalog (a head that does not show them yet). <see cref="Repositories.RepositoryLoad"/> per
        /// repository is reported to <paramref name="onRepository"/>, for its status in the source list.
        /// </param>
        public static async Task<List<ProviderEntry>> ResolveAsync(
            HttpClient http, IEnumerable<ProviderEntry> manifestProviders, IEnumerable<UserSource> sources, CancellationToken cancellationToken = default,
            Repositories.SignedRepositories? repositories = null, Action<Repositories.RepositoryLoad>? onRepository = null)
        {
            var list = sources.ToList();
            var result = Merge(manifestProviders, list);
            var urls = new HashSet<string>(result.Select(p => p.Url ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (var source in list.Where(s => IsFeed(s.Url)))
            {
                foreach (var provider in await FeedProvidersAsync(http, source, cancellationToken).ConfigureAwait(false))
                    AddProvider(result, urls, provider);
            }
            if (repositories is not null)
            {
                // Listed next to the rest, never merged by URL: two sources offering the same app are
                // both listed, each with its own label (#1018).
                foreach (var source in list.Where(IsApprovedRepository))
                {
                    var load = await repositories.LoadAsync(source, cancellationToken).ConfigureAwait(false);
                    onRepository?.Invoke(load);
                    result.AddRange(load.Providers);
                }
            }
            return result;
        }

        /// <summary>
        /// The providers a feed publishes: its .json read as a <see cref="ProviderManifest"/> (or a
        /// bare array of <see cref="ProviderEntry"/>), each marked the user's own and given an id
        /// under this source's (<c>user-&lt;feed&gt;-&lt;id&gt;</c>), so the snapshot and the download
        /// counts never match one. A provider without a URL is dropped; the feed's own category,
        /// cert level and build info are kept. Empty when the feed cannot be read or parsed.
        /// </summary>
        public static async Task<List<ProviderEntry>> FeedProvidersAsync(HttpClient http, UserSource source, CancellationToken cancellationToken = default)
        {
            var url = ApiUrl(source.Url);
            if (url is null || !IsFeed(source.Url))
                return new List<ProviderEntry>();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Apps2Samsung");
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return FeedProviders(source, json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"[UserSources] Feed {url} could not be read: {ex.Message}");
                return new List<ProviderEntry>();
            }
        }

        /// <summary>The providers in a feed's JSON, as <see cref="FeedProvidersAsync"/> shapes them; empty when it does not parse.</summary>
        public static List<ProviderEntry> FeedProviders(UserSource source, string json)
        {
            List<ProviderEntry>? providers = null;
            try
            {
                var trimmed = json.TrimStart();
                if (trimmed.StartsWith('['))
                    providers = JsonSerializer.Deserialize<List<ProviderEntry>>(json, JsonSerializerOptionsProvider.Default);
                else
                    providers = JsonSerializer.Deserialize<ProviderManifest>(json, JsonSerializerOptionsProvider.Default)?.Providers;
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Trace.WriteLine($"[UserSources] Feed {source.Url} does not parse: {ex.Message}");
            }
            if (providers is null)
                return new List<ProviderEntry>();

            var feedId = Id(source);
            var result = new List<ProviderEntry>();
            var index = 0;
            foreach (var provider in providers)
            {
                index++;
                if (provider is null || string.IsNullOrWhiteSpace(provider.Url))
                    continue;
                provider.Url = provider.Url.Trim();
                provider.Id = $"{feedId}-{(string.IsNullOrWhiteSpace(provider.Id) ? index.ToString() : provider.Id.Trim())}";
                provider.Category = AppCategories.Normalize(provider.Category);
                if (provider.Take < 1)
                    provider.Take = 1;
                // A feed provider says Partner for itself; the feed's own flag covers the ones that don't.
                if (source.Partner && string.IsNullOrWhiteSpace(provider.CertLevel))
                    provider.CertLevel = "partner";
                provider.IsUserSource = true;
                // Labelled with the feed it came from, not the app's own name (#1018).
                provider.Source = new CatalogSource(CatalogSourceKind.Unverified, DisplayName(source));
                result.Add(provider);
            }
            return result;
        }

        private static void AddProvider(List<ProviderEntry> result, HashSet<string> urls, ProviderEntry? provider)
        {
            if (provider is not null && urls.Add(provider.Url))
                result.Add(provider);
        }

        private static bool IsPackageName(string name)
            => name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase);

        private static bool IsFeedName(string name) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        // The one place that reads a typed URL. Accepted shapes:
        //   owner/repo                                   github.com/owner/repo[.git][/releases[/latest]]
        //   github.com/owner/repo/releases/tag/<tag>     api.github.com/repos/owner/repo[/releases[/tags/<tag>]]
        //   https://host/path/App.wgt  (any host; a GitHub release asset link is one of these)
        //   https://host/path/feed.json  (any host, raw.githubusercontent.com included): a provider manifest
        private static (UserSourceKind Kind, string? ApiUrl, string? Slug) Normalize(string? input)
        {
            var text = input?.Trim() ?? string.Empty;
            if (text.Length == 0)
                return (UserSourceKind.None, null, null);

            if (SlugPattern.IsMatch(text))
                return Repo(text.Split('/')[0], text.Split('/')[1], null);

            var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return (UserSourceKind.None, null, null);

            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var host = uri.Host.ToLowerInvariant();

            // A link to the file itself, wherever it lives, GitHub's release asset links included.
            if (parts.Length > 0 && IsPackageName(Uri.UnescapeDataString(parts[^1])))
                return (UserSourceKind.DirectFile, uri.AbsoluteUri, null);

            // A feed: a .json wherever it lives (a raw.githubusercontent.com link to a repo's file too).
            if (parts.Length > 0 && IsFeedName(Uri.UnescapeDataString(parts[^1])))
                return (UserSourceKind.Feed, uri.AbsoluteUri, null);

            // A signed repository: HTTPS only, the key is fetched next to it and pinned (#1018).
            if (parts.Length > 0 && Uri.UnescapeDataString(parts[^1]).EndsWith(".signed", StringComparison.OrdinalIgnoreCase))
                return uri.Scheme == Uri.UriSchemeHttps ? (UserSourceKind.SignedRepository, uri.AbsoluteUri, null) : (UserSourceKind.None, null, null);

            if (host is "github.com" or "www.github.com")
            {
                if (parts.Length < 2)
                    return (UserSourceKind.None, null, null);
                var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
                string? tag = null;
                if (parts.Length >= 5 && parts[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
                    && parts[3].Equals("tag", StringComparison.OrdinalIgnoreCase))
                    tag = Uri.UnescapeDataString(parts[4]);
                return Repo(parts[0], repo, tag);
            }

            if (host == "api.github.com")
            {
                // /repos/{owner}/{repo}[/releases[/tags/{tag}]]
                if (parts.Length < 3 || !parts[0].Equals("repos", StringComparison.OrdinalIgnoreCase))
                    return (UserSourceKind.None, null, null);
                string? tag = null;
                if (parts.Length >= 6 && parts[3].Equals("releases", StringComparison.OrdinalIgnoreCase)
                    && parts[4].Equals("tags", StringComparison.OrdinalIgnoreCase))
                    tag = Uri.UnescapeDataString(parts[5]);
                return Repo(parts[1], parts[2], tag);
            }

            return (UserSourceKind.None, null, null);

            static (UserSourceKind, string?, string?) Repo(string owner, string repo, string? tag)
            {
                var slug = $"{owner}/{repo}";
                var api = $"https://api.github.com/repos/{slug}/releases";
                return tag is null
                    ? (UserSourceKind.GitHubReleases, api, slug)
                    : (UserSourceKind.GitHubReleaseTag, $"{api}/tags/{Uri.EscapeDataString(tag)}", slug);
            }
        }
    }
}
