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
    /// App-agnostic launcher-icon patcher, shared by both heads. The user's per-app choice lives in
    /// <see cref="IAppConfig.CustomAppIconsJson"/> as a map <c>{ appKey -> value }</c>, where value is
    /// either the sentinel <c>"oblong"</c> (use the head's bundled 16:9 tile via
    /// <see cref="IOblongIconSource"/>) or a custom PNG file path. An entry applies when its key is
    /// contained in the package file name (case-insensitive). Register last in the patcher pipeline so
    /// a chosen icon overrides the package's default.
    /// </summary>
    public sealed class CustomIconPackagePatcher : IPackagePatcher
    {
        public const string OblongValue = "oblong";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly IAppConfig _config;
        private readonly IOblongIconSource _oblong;

        public CustomIconPackagePatcher(IAppConfig config, IOblongIconSource oblong)
        {
            _config = config;
            _oblong = oblong;
        }

        public bool CanHandle(string packagePath) => Resolve(packagePath) != null;

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            var choice = Resolve(packagePath);
            if (choice == null)
                return InstallResult.SuccessResult();

            using var ws = PackageWorkspace.Extract(packagePath);

            if (choice.Value.CustomPath != null)
                await WgtIconPatcher.SwapLauncherIconAsync(ws, choice.Value.CustomPath);
            else
                await WgtIconPatcher.SwapLauncherIconAsync(ws, choice.Value.Open!, OblongValue, choice.Value.Fallback!);

            ws.Repack();

            Trace.WriteLine($"[CustomIcon] Applied {(choice.Value.CustomPath ?? OblongValue)} icon to {Path.GetFileName(packagePath)}.");
            return InstallResult.SuccessResult();
        }

        // The icon action configured for this package (first map entry whose key matches the file name), or null.
        private (string? CustomPath, Func<Stream>? Open, string? Fallback)? Resolve(string packagePath)
        {
            var map = LoadMap();
            if (map.Count == 0)
                return null;

            var fileName = Path.GetFileName(packagePath);

            foreach (var (key, value) in map)
            {
                if (string.IsNullOrWhiteSpace(value) ||
                    fileName.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (string.Equals(value, OblongValue, StringComparison.OrdinalIgnoreCase))
                {
                    var oblong = _oblong.TryGetOblong(fileName);
                    if (oblong != null)
                        return (null, oblong.Value.OpenStream, oblong.Value.FallbackIconFile);

                    // "oblong" chosen but this head has no bundled tile for this app — leave as-is.
                    continue;
                }

                if (File.Exists(value))
                    return (value, null, null);
            }

            return null;
        }

        private Dictionary<string, string> LoadMap()
        {
            var json = _config.CustomAppIconsJson;
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
                Trace.WriteLine($"[CustomIcon] Failed to parse CustomAppIconsJson: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
