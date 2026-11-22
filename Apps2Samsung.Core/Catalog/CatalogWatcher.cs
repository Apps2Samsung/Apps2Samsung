using System;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// Notices a newer catalog while the app is open (#1157). The catalog on apps2samsung.com is
    /// rebuilt every half hour; a head used to read it once at start, so anything added later only
    /// showed after a restart. A head calls <see cref="MarkShown"/> when it has built its list, and
    /// <see cref="HasNewerAsync"/> on a timer and when it comes back to the front; a true answer
    /// means the list on screen is older than what the server has now.
    /// </summary>
    public sealed class CatalogWatcher
    {
        /// <summary>How often a head asks while it is open. The download itself is reused for ten minutes by the service.</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

        private readonly ResolvedCatalogService _catalog;
        private DateTimeOffset? _shown;
        private bool _hasList;

        public CatalogWatcher(ResolvedCatalogService catalog) => _catalog = catalog;

        /// <summary>The list on screen was just built from what the service holds now.</summary>
        public void MarkShown()
        {
            _shown = _catalog.CurrentGeneratedAt;
            _hasList = true;
        }

        /// <summary>
        /// True when the server has a catalog generated after the one the list was built from.
        /// Never throws; false while there is no list yet, or the catalog cannot be reached.
        /// </summary>
        public async Task<bool> HasNewerAsync(CancellationToken cancellationToken = default)
        {
            if (!_hasList)
                return false;
            try
            {
                var current = await _catalog.GetAsync(cancellationToken);
                if (current is null)
                    return false;
                return _shown is null || current.GeneratedAt > _shown;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
