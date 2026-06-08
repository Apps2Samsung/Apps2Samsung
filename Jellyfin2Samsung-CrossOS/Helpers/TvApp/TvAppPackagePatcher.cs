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
    /// Rewrites the channel list inside a TVApp (KaashDev/TVapp) package before install.
    /// TVApp ships with a placeholder <c>var channels = [...]</c> array in <c>js/main.js</c>;
    /// this replaces it with the channels the user configured in the TVApp settings section.
    /// </summary>
    public class TvAppPackagePatcher : IPackagePatcher
    {
        private const string MainJsRelativePath = "js/main.js";

        // Matches `var channels = [ ... ];` (smallest span, across newlines).
        private static readonly Regex ChannelsArrayRegex =
            new(@"var\s+channels\s*=\s*\[.*?\];", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        public bool CanHandle(string packagePath)
            => Path.GetFileName(packagePath).Contains("tvapp", StringComparison.OrdinalIgnoreCase);

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            var channels = LoadConfiguredChannels();
            if (channels.Count == 0)
            {
                Trace.WriteLine("[TvApp] No channels configured; leaving package unchanged.");
                return InstallResult.SuccessResult();
            }

            using var ws = PackageWorkspace.Extract(packagePath);

            var mainJsPath = Path.Combine(ws.Root, "js", "main.js");
            if (!File.Exists(mainJsPath))
            {
                Trace.WriteLine($"[TvApp] {MainJsRelativePath} not found in package; skipping.");
                return InstallResult.SuccessResult();
            }

            var js = await File.ReadAllTextAsync(mainJsPath);

            if (!ChannelsArrayRegex.IsMatch(js))
            {
                Trace.WriteLine("[TvApp] channels array not found in main.js; skipping.");
                return InstallResult.SuccessResult();
            }

            // JSON is valid JS; serialize with lowercase keys to match TVApp's { name, url } shape.
            var payload = JsonSerializer.Serialize(
                channels.ConvertAll(c => new { name = c.Name ?? string.Empty, url = c.Url ?? string.Empty }));

            js = ChannelsArrayRegex.Replace(js, $"var channels = {payload};", 1);

            await File.WriteAllTextAsync(mainJsPath, js);
            ws.Repack();

            Trace.WriteLine($"[TvApp] Injected {channels.Count} channel(s) into {MainJsRelativePath}.");
            return InstallResult.SuccessResult();
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
