using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace Apps2Samsung.Helpers
{
    /// <summary>
    /// One <see cref="Bitmap"/> per cached catalog icon file (<c>CatalogIcons</c>, #1000), shared by
    /// the list rows and the app sheet: 200 Jellyfin rows showing one icon decode it once.
    /// </summary>
    public static class CatalogIconBitmaps
    {
        private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

        /// <summary>The bitmap for an icon file, null for no file or one Avalonia cannot decode.</summary>
        public static Bitmap? For(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var cached))
                    return cached;
                Bitmap? bitmap = null;
                try
                {
                    bitmap = new Bitmap(path);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[CatalogIconBitmaps] cannot decode {path}: {ex.Message}");
                }
                Cache[path] = bitmap;
                return bitmap;
            }
        }
    }
}
