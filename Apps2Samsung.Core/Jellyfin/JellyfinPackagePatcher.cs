using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin.CSS;
using Apps2Samsung.Helpers.Jellyfin.Diagnostic;
using Apps2Samsung.Helpers.Jellyfin.Fixes;
using Apps2Samsung.Helpers.Jellyfin.Patches;
using Apps2Samsung.Helpers.Jellyfin.Plugins;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin
{
    public class JellyfinPackagePatcher : IPackagePatcher
    {
        private readonly IAppConfig _config;
        private readonly JellyfinIndex _indexHtml;
        private readonly JellyfinDiagnostic _diagnostic;
        private readonly FixYouTube _youTube;
        private readonly CustomCss _customCss;

        public JellyfinPackagePatcher(HttpClient http, IAppConfig config)
        {
            _config = config;

            var api = new JellyfinApiClient(http);
            var plugins = new PluginManager(http, api);

            _indexHtml = new JellyfinIndex(http, api, plugins, config);
            _diagnostic = new JellyfinDiagnostic(config);
            _youTube = new FixYouTube();
            _customCss = new CustomCss(config);
        }

        public bool CanHandle(string packagePath)
            => Path.GetFileName(packagePath)
                   .Contains(Constants.AppIdentifiers.JellyfinAppName, StringComparison.OrdinalIgnoreCase);

        public Task<InstallResult> ApplyAsync(string packagePath)
        {
            // No server configured → nothing to inject (preserves prior install behavior).
            if (string.IsNullOrEmpty(_config.JellyfinFullUrl))
                return Task.FromResult(InstallResult.SuccessResult());

            return ApplyJellyfinConfigAsync(packagePath);
        }

        public async Task<InstallResult> ApplyJellyfinConfigAsync(string packagePath)
        {
            using var ws = PackageWorkspace.Extract(packagePath);

            // Apply server scripts (JS injection) if enabled
            if (_config.UseServerScripts)
                await _indexHtml.PatchIndexAsync(ws, _config.JellyfinFullUrl);

            // Apply YouTube plugin patch if enabled
            if (_config.PatchYoutubePlugin)
            {
                await _youTube.PatchPluginAsync(ws);
                await _youTube.UpdateCorsAsync(ws);
                await _youTube.CreateYouTubeResolverAsync(ws);
            }

            // Always update server address
            await _indexHtml.UpdateServerAddressAsync(ws);

            // Inject auto-login credentials if available
            if (!string.IsNullOrEmpty(_config.JellyfinAccessToken) &&
                !string.IsNullOrEmpty(_config.JellyfinUserId))
            {
                Trace.WriteLine("Injecting auto-login credentials...");
                await _indexHtml.InjectAutoLoginAsync(ws);
            }

            if (_config.EnableDevLogs)
            {
                Trace.WriteLine("Injecting dev logs...");
                await _diagnostic.InjectDevLogsAsync(ws);
            }

            // Inject custom CSS if configured
            if (!string.IsNullOrWhiteSpace(_config.CustomCss))
            {
                Trace.WriteLine("Injecting custom CSS...");
                await _customCss.InjectAsync(ws);
            }

            ws.Repack();
            return InstallResult.SuccessResult();
        }
    }
}
