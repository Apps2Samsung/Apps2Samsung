using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Models
{
    public sealed class TvAppIconManifest
    {
        [JsonPropertyName("items")]
        public List<TvAppIconEntry> Items { get; set; } = new();
    }

    public sealed class TvAppIconEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;
    }
}
