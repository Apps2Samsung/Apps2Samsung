using System;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// Fills in <see cref="GitHubRelease.SourceUrl"/> and <see cref="GitHubRelease.Author"/> (#820):
    /// from the community catalog's <c>repo</c> + <c>host</c> for a community package, from the
    /// provider's GitHub API URL for an app of its own (Jellyfin, Moonfin, …).
    /// </summary>
    public static class SourceRepo
    {
        /// <summary>A catalog entry's <c>owner/repo</c> on <paramref name="host"/> (github, the default, or gitlab).</summary>
        public static void Stamp(GitHubRelease release, string? repo, string? host)
        {
            var slug = repo?.Trim().Trim('/');
            var slash = slug?.IndexOf('/') ?? -1;
            if (slug is null || slash <= 0 || slash == slug.Length - 1)
                return;

            var site = string.Equals(host, "gitlab", StringComparison.OrdinalIgnoreCase) ? "https://gitlab.com" : "https://github.com";
            release.SourceUrl = $"{site}/{slug}";
            release.Author = slug.Substring(0, slash);
        }

        /// <summary>
        /// A provider URL such as <c>https://api.github.com/repos/owner/repo/releases</c>. Anything
        /// that isn't a GitHub API repository URL leaves the release as it is.
        /// </summary>
        public static void StampFromApiUrl(GitHubRelease release, string? apiUrl)
        {
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
                return;

            // /repos/{owner}/{repo}/...
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && string.Equals(parts[0], "repos", StringComparison.OrdinalIgnoreCase))
                Stamp(release, $"{parts[1]}/{parts[2]}", "github");
        }
    }
}
