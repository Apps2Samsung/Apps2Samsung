using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// A source the user added themselves (Settings > Installing > Your sources, the catalog's ⋯ >
    /// Add a source, <c>apps2samsung-cli sources add</c>): a GitHub repository or one of its releases,
    /// whose .wgt/.tpk assets are listed like any provider's, or a direct link to one such file. For a
    /// developer that is the way to install a build before, or without, it being in
    /// tizen-community-packages. Stored as JSON in the head's settings, read on every catalog load,
    /// so Refresh and a restart pick up a new release.
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

        /// <summary>The name a source lists under when the user gave none: the repository or the file.</summary>
        public static string DefaultName(string? url)
        {
            var (kind, api, slug) = Normalize(url);
            return kind switch
            {
                UserSourceKind.DirectFile => Path.GetFileNameWithoutExtension(FileName(url)),
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
        /// loop as the built-in ones. Null when its URL is not something the installer can read.
        /// </summary>
        public static ProviderEntry? ToProvider(UserSource source)
        {
            var api = ApiUrl(source.Url);
            if (api is null)
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
            };
        }

        /// <summary>
        /// The manifest's providers followed by the user's, skipping a user source that fetches a URL
        /// the manifest already lists (the app would appear twice) and a second copy of the same
        /// user source.
        /// </summary>
        public static List<ProviderEntry> Merge(IEnumerable<ProviderEntry> manifestProviders, IEnumerable<UserSource> sources)
        {
            var result = manifestProviders.ToList();
            var urls = new HashSet<string>(result.Select(p => p.Url ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                var provider = ToProvider(source);
                if (provider is not null && urls.Add(provider.Url))
                    result.Add(provider);
            }
            return result;
        }

        private static bool IsPackageName(string name)
            => name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase);

        // The one place that reads a typed URL. Accepted shapes:
        //   owner/repo                                   github.com/owner/repo[.git][/releases[/latest]]
        //   github.com/owner/repo/releases/tag/<tag>     api.github.com/repos/owner/repo[/releases[/tags/<tag>]]
        //   https://host/path/App.wgt  (any host; a GitHub release asset link is one of these)
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
