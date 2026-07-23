using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core; // PackageWorkspace

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Swaps a Tizen package's launcher icon (the file referenced by config.xml's
    /// <c>&lt;icon src&gt;</c>) for another image. config.xml itself is left untouched; the package is
    /// re-signed after patching, so overwriting the icon bytes is enough. Head-agnostic: the source
    /// image is supplied either as a file path (a user-picked PNG) or as a stream opener (a head's
    /// bundled asset — e.g. the desktop's Avalonia <c>avares://</c> "oblong" tiles).
    /// </summary>
    public static class WgtIconPatcher
    {
        // Captures the icon filename from config.xml's <icon src="..."/>.
        private static readonly Regex IconSrcRegex =
            new(@"<icon\b[^>]*\bsrc\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Swaps the launcher icon with a user-supplied image file (e.g. a custom PNG).</summary>
        public static Task SwapLauncherIconAsync(PackageWorkspace ws, string imageFilePath, string fallbackIconFile = "icon.png")
            => WriteIconAsync(ws, () => File.OpenRead(imageFilePath), imageFilePath, fallbackIconFile);

        /// <summary>Swaps the launcher icon with an image from a caller-supplied stream (e.g. a bundled
        /// asset the head opens itself, keeping this Core helper free of any UI-framework dependency).</summary>
        public static Task SwapLauncherIconAsync(PackageWorkspace ws, Func<Stream> openSource, string sourceLabel, string fallbackIconFile = "icon.png")
            => WriteIconAsync(ws, openSource, sourceLabel, fallbackIconFile);

        private static async Task WriteIconAsync(PackageWorkspace ws, Func<Stream> openSource, string sourceLabel, string fallbackIconFile)
        {
            var iconFile = ResolveIconFileName(ws) ?? fallbackIconFile;
            var iconPath = Path.Combine(ws.Root, iconFile.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);

                await using var source = openSource();
                await using var dest = File.Create(iconPath);
                await source.CopyToAsync(dest);

                Trace.WriteLine($"[WgtIcon] Swapped launcher icon ({iconFile}) for {sourceLabel}.");
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
