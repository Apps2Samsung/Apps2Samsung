using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers
{
    /// <summary>
    /// App-agnostic icon patcher. The user's per-app launcher-icon choice lives in
    /// <see cref="AppSettings.CustomAppIconsJson"/> as a map <c>{ appKey -> value }</c>, where value is
    /// either the sentinel <c>"oblong"</c> (use the app's bundled 16:9 tile) or a custom PNG file path.
    /// An entry applies to a package when its key is contained in the package file name (case-insensitive).
    /// Registered last in the patcher pipeline so a chosen icon overrides the package's default.
    /// </summary>
    public class CustomIconPackagePatcher : IPackagePatcher
    {
        private const string OblongValue = "oblong";

        // Apps that ship a bundled 16:9 "oblong" tile, keyed by a token found in their package file name.
        private static readonly (string Token, Uri Asset, string Fallback)[] OblongAssets =
        {
            ("tvapp", new Uri("avares://Apps2Samsung/Assets/TvApp/oblong-icon.png"), "noun-live-tv-3548799.png"),
            ("litefin", new Uri("avares://Apps2Samsung/Assets/Litefin/oblong-icon.png"), "icon.png"),
        };

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public bool CanHandle(string packagePath) => Resolve(packagePath) != null;

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            var choice = Resolve(packagePath);
            if (choice == null)
                return InstallResult.SuccessResult();

            using var ws = PackageWorkspace.Extract(packagePath);

            if (choice.Value.IsOblong)
                await WgtIconPatcher.SwapLauncherIconAsync(ws, choice.Value.Asset!, choice.Value.Fallback!);
            else
                await WgtIconPatcher.SwapLauncherIconAsync(ws, choice.Value.Path!);

            ws.Repack();

            Trace.WriteLine($"[CustomIcon] Applied {(choice.Value.IsOblong ? "oblong" : choice.Value.Path)} icon to {Path.GetFileName(packagePath)}.");
            return InstallResult.SuccessResult();
        }

        // The icon action configured for this package (first map entry whose key matches the file name), or null.
        private static (bool IsOblong, Uri? Asset, string? Fallback, string? Path)? Resolve(string packagePath)
        {
            var map = LoadMap();
            if (map.Count == 0)
                return null;

            var fileName = System.IO.Path.GetFileName(packagePath);

            foreach (var (key, value) in map)
            {
                if (string.IsNullOrWhiteSpace(value) ||
                    fileName.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (string.Equals(value, OblongValue, StringComparison.OrdinalIgnoreCase))
                {
                    var bundled = OblongAssets.FirstOrDefault(
                        a => fileName.IndexOf(a.Token, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (bundled.Asset != null)
                        return (true, bundled.Asset, bundled.Fallback, null);

                    // "oblong" chosen but this app ships no bundled tile — leave the package as-is.
                    continue;
                }

                if (File.Exists(value))
                    return (false, null, null, value);
            }

            return null;
        }

        private static Dictionary<string, string> LoadMap()
        {
            var json = AppSettings.Default.CustomAppIconsJson;
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                       is { } parsed
                    ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CustomIcon] Failed to parse CustomAppIconsJson: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
