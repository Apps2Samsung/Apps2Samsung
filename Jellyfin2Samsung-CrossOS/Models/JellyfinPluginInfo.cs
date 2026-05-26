using System;
using System.Collections.Generic;

namespace Jellyfin2Samsung.Models
{
    public class JellyfinPluginInfo
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string? Version { get; set; }
    }

    public class PluginMatrixEntry
    {
        public string Name { get; set; }
        public List<string> ExplicitServerFiles { get; set; }
        public List<string> FallbackUrls { get; set; }
        public bool UseBabel { get; set; }
        public string RawRoot { get; set; }
    }
    public class ExtractedDomBlocks
    {
        public List<string> HeadInjectBlocks { get; set; } = new();
        public List<string> BodyInjectBlocks { get; set; } = new();
    }
    public enum ServerAssetKind
    {
        Unknown = 0,
        PluginAsset = 1
    }

    public sealed record ServerAssetRule(string pluginName, Func<string, bool> match, ServerAssetKind treatAs);
}