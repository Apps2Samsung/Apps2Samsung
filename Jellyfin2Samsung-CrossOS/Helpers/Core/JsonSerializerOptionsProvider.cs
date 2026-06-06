using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Provides centralized, pre-configured JsonSerializerOptions for consistent JSON serialization
    /// throughout the application. This eliminates inconsistent settings and improves performance
    /// by reusing options instances.
    /// </summary>
    public static class JsonSerializerOptionsProvider
    {
        /// <summary>
        /// Default options for general JSON serialization/deserialization.
        /// - Case-insensitive property matching
        /// - Camel case naming policy
        /// - Ignores null values when writing
        /// </summary>
        public static JsonSerializerOptions Default { get; } = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>
        /// Options for pretty-printed JSON output (e.g., config files, logs).
        /// Same as Default but with indentation enabled.
        /// </summary>
        public static JsonSerializerOptions Indented { get; } = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        /// <summary>
        /// Options optimized for GitHub API responses.
        /// Uses snake_case naming policy to match GitHub's JSON format.
        /// </summary>
        public static JsonSerializerOptions GitHub { get; } = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>
        /// Options for strict JSON parsing (no case-insensitive matching).
        /// Use when exact property name matching is required.
        /// </summary>
        public static JsonSerializerOptions Strict { get; } = new()
        {
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>
        /// Options for web/API responses with standard web conventions.
        /// </summary>
        public static JsonSerializerOptions Web { get; } = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
