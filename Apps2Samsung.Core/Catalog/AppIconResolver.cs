using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// Store icons for the installed-apps list, looked up by Tizen id or by app name. The catalogue
    /// itself — where it is fetched from, and the embedded copy used offline — is
    /// <see cref="SamsungTvAppCatalog"/>; this only reshapes it into the lookup the UI wants. An app
    /// the catalogue doesn't know gets no icon, and the UI draws its lettered avatar instead.
    /// </summary>
    public static class AppIconResolver
    {
        private static Dictionary<string, string>? _iconMapCache;

        /// <summary>
        /// Maps every known Tizen id — and every known app name, for apps the TV reports by name only —
        /// to an icon URL. Built once per run from the catalogue.
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, string>> GetIconMapAsync()
        {
            if (_iconMapCache != null)
                return _iconMapCache;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var app in await SamsungTvAppCatalog.GetAsync())
            {
                if (string.IsNullOrWhiteSpace(app.IconUrl))
                    continue;

                // An app can be known by several ids across firmware generations; all of them resolve
                // to the same icon.
                foreach (var id in app.Ids)
                    map[id] = app.IconUrl;

                // Also by name, for the rows where the TV reported a title but no id we recognise.
                if (!string.IsNullOrWhiteSpace(app.Name))
                    map[app.Name] = app.IconUrl;
            }

            _iconMapCache = map;
            return _iconMapCache;
        }

        /// <summary>
        /// Attempts to get the icon URL for a given Tizen ID. Returns null if not found.
        /// </summary>
        public static async Task<string?> TryGetIconUrlAsync(string tizenId)
        {
            if (string.IsNullOrWhiteSpace(tizenId))
                return null;

            var map = await GetIconMapAsync();
            return map.TryGetValue(tizenId, out var iconUrl) ? iconUrl : null;
        }
    }
}
