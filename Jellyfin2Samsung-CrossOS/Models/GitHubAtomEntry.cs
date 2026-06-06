using System;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// Represents a release entry parsed from the GitHub Atom feed.
    /// The Atom feed does not have rate limits unlike the REST API.
    /// </summary>
    public class GitHubAtomEntry
    {
        /// <summary>
        /// The unique ID of the release (e.g., "tag:github.com,2008:Repository/123456/v1.0.0").
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// The release title/name.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// When the release was last updated.
        /// </summary>
        public DateTime? Updated { get; set; }

        /// <summary>
        /// The link to the release page.
        /// </summary>
        public string Link { get; set; } = string.Empty;

        /// <summary>
        /// The release content/description (HTML).
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// The author who published the release.
        /// </summary>
        public string AuthorName { get; set; } = string.Empty;

        /// <summary>
        /// Extracts the tag name (version) from the release ID or link.
        /// </summary>
        public string TagName
        {
            get
            {
                // Try to extract from link first: https://github.com/owner/repo/releases/tag/v1.0.0
                if (!string.IsNullOrEmpty(Link))
                {
                    const string tagMarker = "/releases/tag/";
                    var tagIndex = Link.IndexOf(tagMarker, StringComparison.OrdinalIgnoreCase);
                    if (tagIndex >= 0)
                    {
                        return Link.Substring(tagIndex + tagMarker.Length);
                    }
                }

                // Fallback: extract from ID: tag:github.com,2008:Repository/123456/v1.0.0
                if (!string.IsNullOrEmpty(Id))
                {
                    var lastSlash = Id.LastIndexOf('/');
                    if (lastSlash >= 0 && lastSlash < Id.Length - 1)
                    {
                        return Id.Substring(lastSlash + 1);
                    }
                }

                return string.Empty;
            }
        }
    }
}
