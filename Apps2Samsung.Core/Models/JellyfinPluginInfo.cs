using System;
using System.Collections.Generic;

namespace Apps2Samsung.Models
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

        /// <summary>
        /// Lowest plugin version whose client script works inside the packaged app.
        /// Null means every version is accepted. Older installs are listed as unsupported
        /// and skipped at patch time instead of shipping a script that cannot reach the server.
        /// </summary>
        public Version? MinimumVersion { get; set; }
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