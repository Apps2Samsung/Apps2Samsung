using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.TvApp
{
    /// <summary>
    /// Applies the user's TVApp (KaashDev/TVapp) configuration to the package before install:
    ///  - rewrites the placeholder <c>var channels = [...]</c> array in <c>js/main.js</c>, and
    ///  - optionally swaps the launcher icon for a 16:9 "oblong" variant (older Tizen 5.5 TVs).
    /// </summary>
    public class TvAppPackagePatcher : IPackagePatcher
    {
        private const string MainJsRelativePath = "js/main.js";
        private static readonly Uri OblongIconUri = new("avares://Apps2Samsung/Assets/TvApp/oblong-icon.png");

        // Matches `var channels = [ ... ];` (smallest span, across newlines).
        private static readonly Regex ChannelsArrayRegex =
            new(@"var\s+channels\s*=\s*\[.*?\];", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        public bool CanHandle(string packagePath)
            => Path.GetFileName(packagePath).Contains("tvapp", StringComparison.OrdinalIgnoreCase);

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            var channels = LoadConfiguredChannels();
            var useOblongIcon = AppSettings.Default.TvAppUseOblongIcon;

            if (channels.Count == 0 && !useOblongIcon)
            {
                Trace.WriteLine("[TvApp] Nothing configured; leaving package unchanged.");
                return InstallResult.SuccessResult();
            }

            using var ws = PackageWorkspace.Extract(packagePath);

            if (channels.Count > 0)
                await PatchChannelsAsync(ws, channels);

            if (useOblongIcon)
                await WgtIconPatcher.SwapLauncherIconAsync(ws, OblongIconUri, "noun-live-tv-3548799.png");

            ws.Repack();
            return InstallResult.SuccessResult();
        }

        private static async Task PatchChannelsAsync(PackageWorkspace ws, List<TvAppChannel> channels)
        {
            var mainJsPath = Path.Combine(ws.Root, "js", "main.js");
            if (!File.Exists(mainJsPath))
            {
                Trace.WriteLine($"[TvApp] {MainJsRelativePath} not found in package; skipping channels.");
                return;
            }

            var js = await File.ReadAllTextAsync(mainJsPath);

            if (!ChannelsArrayRegex.IsMatch(js))
            {
                Trace.WriteLine("[TvApp] channels array not found in main.js; skipping channels.");
                return;
            }

            // JSON is valid JS; serialize with lowercase keys to match TVApp's { name, url } shape.
            var payload = JsonSerializer.Serialize(
                channels.ConvertAll(c => new { name = c.Name ?? string.Empty, url = c.Url ?? string.Empty }));

            js = ChannelsArrayRegex.Replace(js, $"var channels = {payload};", 1);

            await File.WriteAllTextAsync(mainJsPath, js);
            Trace.WriteLine($"[TvApp] Injected {channels.Count} channel(s) into {MainJsRelativePath}.");
        }

        private static List<TvAppChannel> LoadConfiguredChannels()
        {
            var json = AppSettings.Default.TvAppChannelsJson;
            if (string.IsNullOrWhiteSpace(json))
                return new List<TvAppChannel>();

            try
            {
                return JsonSerializer.Deserialize<List<TvAppChannel>>(json, ReadOptions) ?? new List<TvAppChannel>();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TvApp] Failed to parse configured channels: {ex.Message}");
                return new List<TvAppChannel>();
            }
        }
    }
}
