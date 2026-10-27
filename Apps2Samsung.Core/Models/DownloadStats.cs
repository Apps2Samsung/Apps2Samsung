using System;
using System.Collections.Generic;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// All-time download counts apps2samsung.com serves at <c>/catalog/downloads.json</c>, summed
    /// over every GitHub release of every provider by <c>tools/DownloadStats</c> once a day
    /// (<c>.github/workflows/download-stats.yml</c>). GitHub only counts per release asset, and the
    /// community bundle re-uploads every app in each <c>community-N</c> release, so the per-release
    /// numbers reset with every sync; this file is the sum. A separate file from
    /// <see cref="ResolvedCatalog"/> because counts change all the time and the snapshot only
    /// commits when its content changes.
    /// </summary>
    public sealed class DownloadStats
    {
        /// <summary>The schema a client must understand to use the file.</summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public DateTimeOffset GeneratedAt { get; set; }
        /// <summary>The day the <c>recent</c> counts start from: 30 days back, or the first day the generator ran when that is later.</summary>
        public string? RecentSince { get; set; }
        /// <summary>Per manifest provider id.</summary>
        public Dictionary<string, ProviderDownloads> Providers { get; set; } = new();
    }

    public sealed class ProviderDownloads
    {
        /// <summary>Every .wgt/.tpk download over all counted releases.</summary>
        public long Total { get; set; }
        /// <summary>The part of <see cref="Total"/> since <see cref="DownloadStats.RecentSince"/>.</summary>
        public long Recent { get; set; }
        /// <summary>
        /// Per file name, for a provider whose files are separate apps (the community bundle,
        /// <c>expandAssets</c>); null for the others, which list as one app.
        /// </summary>
        public Dictionary<string, FileDownloads>? Files { get; set; }
    }

    public sealed class FileDownloads
    {
        public long Total { get; set; }
        public long Recent { get; set; }
    }
}
