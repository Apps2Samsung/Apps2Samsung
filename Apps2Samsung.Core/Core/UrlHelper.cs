using System;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Provides URL manipulation utilities.
    /// Centralizes URL normalization to eliminate duplicate TrimEnd('/') calls across the codebase.
    /// </summary>
    public static class UrlHelper
    {
        /// <summary>
        /// Normalizes a server URL by removing trailing slashes.
        /// </summary>
        /// <param name="url">The URL to normalize.</param>
        /// <returns>The normalized URL without trailing slashes, or empty string if null.</returns>
        public static string NormalizeServerUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            return url.TrimEnd('/');
        }

        /// <summary>
        /// Combines a base URL with a path segment, ensuring proper slash handling.
        /// </summary>
        /// <param name="baseUrl">The base URL (trailing slash will be removed).</param>
        /// <param name="path">The path to append (leading slash will be added if missing).</param>
        /// <returns>The combined URL.</returns>
        public static string CombineUrl(string? baseUrl, string? path)
        {
            var normalizedBase = NormalizeServerUrl(baseUrl);

            if (string.IsNullOrWhiteSpace(path))
                return normalizedBase;

            var normalizedPath = path.TrimStart('/');

            return string.IsNullOrEmpty(normalizedBase)
                ? normalizedPath
                : $"{normalizedBase}/{normalizedPath}";
        }

        /// <summary>
        /// Creates an absolute URI from a base server URL and a relative or absolute path.
        /// </summary>
        /// <param name="serverUrl">The base server URL.</param>
        /// <param name="relativeOrAbsolutePath">A relative path or absolute URL.</param>
        /// <returns>The absolute URI.</returns>
        public static Uri GetAbsoluteUri(string serverUrl, string relativeOrAbsolutePath)
        {
            if (Uri.IsWellFormedUriString(relativeOrAbsolutePath, UriKind.Absolute))
                return new Uri(relativeOrAbsolutePath);

            var baseUri = new Uri(NormalizeServerUrl(serverUrl) + "/");
            return new Uri(baseUri, relativeOrAbsolutePath.TrimStart('/'));
        }

        /// <summary>
        /// Validates if a string is a valid HTTP or HTTPS URL.
        /// </summary>
        /// <param name="url">The URL to validate.</param>
        /// <returns>True if the URL is valid, false otherwise.</returns>
        public static bool IsValidHttpUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
                   && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Extracts the file name from a URL path.
        /// </summary>
        /// <param name="url">The URL to extract the file name from.</param>
        /// <returns>The file name, or empty string if not found.</returns>
        public static string GetFileNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            try
            {
                var uri = new Uri(url);
                return System.IO.Path.GetFileName(uri.LocalPath);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
