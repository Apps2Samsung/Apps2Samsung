using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers
{
    /// <summary>
    /// App-agnostic patcher: if the user picked a custom launcher icon for the app being installed
    /// (keyed by app title in <see cref="AppSettings.CustomAppIconsJson"/>), swap it into the wgt.
    /// Runs last in the patcher pipeline so a custom icon overrides any built-in (e.g. oblong) one.
    /// </summary>
    public class CustomIconPackagePatcher : IPackagePatcher
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public bool CanHandle(string packagePath) => ResolveIconPath(packagePath) != null;

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            var iconPath = ResolveIconPath(packagePath);
            if (iconPath == null)
                return InstallResult.SuccessResult();

            using var ws = PackageWorkspace.Extract(packagePath);
            await WgtIconPatcher.SwapLauncherIconAsync(ws, iconPath);
            ws.Repack();

            Trace.WriteLine($"[CustomIcon] Applied custom icon '{iconPath}' to {Path.GetFileName(packagePath)}.");
            return InstallResult.SuccessResult();
        }

        // The custom icon configured for this package's app title, if it exists on disk.
        private static string? ResolveIconPath(string packagePath)
        {
            var map = LoadMap();
            if (map.Count == 0)
                return null;

            var appTitle = FileHelper.AppTitleFromPackage(packagePath);
            return map.TryGetValue(appTitle, out var path) && !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? path
                : null;
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
