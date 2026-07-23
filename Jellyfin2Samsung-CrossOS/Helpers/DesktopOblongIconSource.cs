using Apps2Samsung.Packaging;
using Avalonia.Platform;
using System;
using System.IO;

namespace Apps2Samsung.Helpers
{
    /// <summary>
    /// Desktop implementation of <see cref="IOblongIconSource"/>: the bundled 16:9 "oblong" launcher
    /// tiles shipped as Avalonia <c>avares://</c> assets. Keeps the framework-specific
    /// <see cref="AssetLoader"/> access in the desktop head so the shared
    /// <see cref="CustomIconPackagePatcher"/> stays UI-framework-free.
    /// </summary>
    public sealed class DesktopOblongIconSource : IOblongIconSource
    {
        // Apps that ship a bundled oblong tile, keyed by a token found in their package file name.
        private static readonly (string Token, Uri Asset, string Fallback)[] OblongAssets =
        {
            ("tvapp", new Uri("avares://Apps2Samsung/Assets/TvApp/oblong-icon.png"), "noun-live-tv-3548799.png"),
            ("litefin", new Uri("avares://Apps2Samsung/Assets/Litefin/oblong-icon.png"), "icon.png"),
        };

        public (Func<Stream> OpenStream, string FallbackIconFile)? TryGetOblong(string packageFileName)
        {
            foreach (var (token, asset, fallback) in OblongAssets)
                if (packageFileName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return (() => AssetLoader.Open(asset), fallback);
            return null;
        }
    }
}
