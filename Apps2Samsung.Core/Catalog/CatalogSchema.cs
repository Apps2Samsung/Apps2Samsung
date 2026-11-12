using System;
using System.Diagnostics;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The official catalog's <c>schemaVersion</c> (tizen-community-packages docs/catalog.md, #257).
    /// Additive changes keep it at 1; a breaking change bumps it, announced 30 days ahead. A catalog
    /// newer than this build knows is still read as far as it goes (unknown fields are skipped), and
    /// the heads say so under the list, so the user knows an update may show more or fix what looks off.
    /// </summary>
    public static class CatalogSchema
    {
        /// <summary>The newest <c>schemaVersion</c> this build understands.</summary>
        public const int Known = 1;

        /// <summary>The newer version the last official catalog carried; null when it is one this build knows.</summary>
        public static int? NewerVersion { get; private set; }

        /// <summary>Takes the official catalog in, as <see cref="AppCategories.Use"/> does. Null (no catalog) clears it.</summary>
        public static void Use(CommunityCatalog? catalog)
        {
            NewerVersion = IsNewer(catalog) ? catalog!.SchemaVersion : null;
            if (NewerVersion is { } v)
                Trace.WriteLine($"[catalog] schemaVersion {v} is newer than this build knows ({Known}); reading what it can");
        }

        public static bool IsNewer(CommunityCatalog? catalog) => catalog is not null && catalog.SchemaVersion > Known;

        /// <summary>The line under the catalog, or null when there is nothing to say.</summary>
        public static string? Notice(Func<string, string> localize) =>
            NewerVersion is { } v ? string.Format(localize("lblCatalogSchemaNewer"), v) : null;
    }
}
