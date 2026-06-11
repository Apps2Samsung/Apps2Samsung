using Avalonia.Platform;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Swaps a Tizen package's launcher icon (the file referenced by config.xml's
    /// <c>&lt;icon src&gt;</c>) for a bundled image — used to give apps a wide 16:9
    /// "oblong" tile on older Tizen 5.5 TVs. config.xml itself is left untouched; the
    /// package is re-signed after patching, so overwriting the icon bytes is enough.
    /// </summary>
    public static class WgtIconPatcher
    {
        // Captures the icon filename from config.xml's <icon src="..."/>.
        private static readonly Regex IconSrcRegex =
            new(@"<icon\b[^>]*\bsrc\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Overwrites the package's launcher icon with the bundled asset at <paramref name="assetUri"/>.
        /// </summary>
        /// <param name="fallbackIconFile">Icon path to use if config.xml has no &lt;icon src&gt;.</param>
        public static async Task SwapLauncherIconAsync(PackageWorkspace ws, Uri assetUri, string fallbackIconFile = "icon.png")
        {
            var iconFile = ResolveIconFileName(ws) ?? fallbackIconFile;
            var iconPath = Path.Combine(ws.Root, iconFile.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);

                await using var asset = AssetLoader.Open(assetUri);
                await using var dest = File.Create(iconPath);
                await asset.CopyToAsync(dest);

                Trace.WriteLine($"[WgtIcon] Swapped launcher icon ({iconFile}) for {assetUri}.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WgtIcon] Failed to swap launcher icon: {ex.Message}");
            }
        }

        private static string? ResolveIconFileName(PackageWorkspace ws)
        {
            var configPath = Path.Combine(ws.Root, "config.xml");
            if (!File.Exists(configPath))
                return null;

            try
            {
                var match = IconSrcRegex.Match(File.ReadAllText(configPath));
                return match.Success ? match.Groups[1].Value : null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WgtIcon] Failed to read config.xml icon src: {ex.Message}");
                return null;
            }
        }
    }
}
