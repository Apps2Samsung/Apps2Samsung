namespace Apps2Samsung.Models
{
    /// <summary>
    /// Represents a JellyTheme CSS theme with its metadata.
    /// </summary>
    public class JellyTheme
    {
        /// <summary>
        /// Display name of the theme (e.g., "Obsidian").
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Emoji icon for the theme.
        /// </summary>
        public string Icon { get; init; } = string.Empty;

        /// <summary>
        /// Color description (e.g., "Purple").
        /// </summary>
        public string ColorName { get; init; } = string.Empty;

        /// <summary>
        /// Hex color code for the button background.
        /// </summary>
        public string HexColor { get; init; } = "#6B5B95";

        /// <summary>
        /// The CSS @import URL for this theme.
        /// </summary>
        public string CssImportUrl { get; init; } = string.Empty;

        /// <summary>
        /// Preview image URL.
        /// </summary>
        public string PreviewUrl { get; init; } = string.Empty;

        /// <summary>
        /// GitHub README URL for this theme.
        /// </summary>
        public string ReadmeUrl { get; init; } = string.Empty;

        /// <summary>
        /// Gets the full @import statement for this theme.
        /// </summary>
        public string CssImportStatement => $"@import url(\"{CssImportUrl}\");";

        /// <summary>
        /// Gets the display text with icon.
        /// </summary>
        public string DisplayName => $"{Icon} {Name}";
    }
}
