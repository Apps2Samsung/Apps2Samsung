using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.Core; // PackageWorkspace
using Apps2Samsung.Interfaces;    // IPackagePatcher
using Apps2Samsung.Models;        // InstallResult

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// App-agnostic launcher-title patcher, shared by both heads. The user's per-app choice lives in
    /// <see cref="IAppConfig.CustomAppTitlesJson"/> as a map <c>{ appKey -> title }</c>. An entry applies
    /// when its key is contained in the package file name (case-insensitive). Mirrors
    /// <see cref="CustomIconPackagePatcher"/>; register alongside it in the patcher pipeline.
    /// </summary>
    public sealed class AppTitlePackagePatcher : IPackagePatcher
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly IAppConfig _config;

        public AppTitlePackagePatcher(IAppConfig config) => _config = config;

        public bool CanHandle(string packagePath) => Resolve(packagePath) != null;

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            var title = Resolve(packagePath);
            if (title == null)
                return InstallResult.SuccessResult();

            using var ws = PackageWorkspace.Extract(packagePath);
            await WgtTitlePatcher.SwapAppTitleAsync(ws, title);
            ws.Repack();

            Trace.WriteLine($"[CustomTitle] Applied title '{title}' to {Path.GetFileName(packagePath)}.");
            return InstallResult.SuccessResult();
        }

        // The custom title configured for this package (first map entry whose key matches the file name), or null.
        private string? Resolve(string packagePath)
        {
            var map = LoadMap();
            if (map.Count == 0)
                return null;

            var fileName = Path.GetFileName(packagePath);
            foreach (var (key, value) in map)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    fileName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return value.Trim();
            }

            return null;
        }

        private Dictionary<string, string> LoadMap()
        {
            var json = _config.CustomAppTitlesJson;
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) is { } parsed
                    ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CustomTitle] Failed to parse CustomAppTitlesJson: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
