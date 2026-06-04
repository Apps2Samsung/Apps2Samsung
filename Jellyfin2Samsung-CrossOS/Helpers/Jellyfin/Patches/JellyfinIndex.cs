using Jellyfin2Samsung.Helpers.API;
using Jellyfin2Samsung.Helpers.Core;
using Jellyfin2Samsung.Helpers.Jellyfin.Plugins;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Jellyfin2Samsung.Helpers.Jellyfin.Patches
{
    public class JellyfinIndex(
        HttpClient http,
        JellyfinApiClient api,
        PluginManager plugins)
    {
        private readonly JellyfinApiClient _apiClient = api;
        private readonly JellyfinPluginPatcher _plugins = new(http, api, plugins);

        public async Task PatchIndexAsync(PackageWorkspace ws, string serverUrl)
        {
            string index = Path.Combine(ws.Root, "www", "index.html");
            if (!File.Exists(index)) return;

            var html = await File.ReadAllTextAsync(index);

            html = HtmlUtils.EnsureBaseHref(html);
            html = HtmlUtils.RewriteLocalPaths(html);

            var css = new StringBuilder();
            var headJs = new StringBuilder();
            var bodyJs = new StringBuilder();

            await _plugins.PatchPluginsAsync(ws, serverUrl, css, headJs, bodyJs);

            html = html.Replace("</head>", css + "\n" + headJs + "\n</head>");
            html = html.Replace("</body>", bodyJs + "\n</body>");

            html = HtmlUtils.CleanAndApplyCsp(html);
            html = HtmlUtils.EnsurePublicJsIsLast(html);

            await File.WriteAllTextAsync(index, html);
        }
        public async Task UpdateServerAddressAsync(PackageWorkspace ws)
        {
            string path = Path.Combine(ws.Root, "www", "config.json");

            JsonObject config;

            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                config = JsonNode.Parse(json)?.AsObject()
                         ?? new JsonObject();
            }
            else
            {
                config = new JsonObject();
            }

            // Ensure multiserver is set
            config["multiserver"] = false;

            // Ensure servers array exists
            if (config["servers"] is not JsonArray servers)
            {
                servers = new JsonArray();
                config["servers"] = servers;
            }

            var serverUrl = UrlHelper.NormalizeServerUrl(AppSettings.Default.JellyfinFullUrl);

            // Avoid duplicates
            if (!servers.Any(s => s?.GetValue<string>() == serverUrl))
                servers.Add(serverUrl);

            // Add LocalAddress (IP-based) as fallback when the primary URL uses mDNS (.local)
            // Samsung TVs (Tizen) cannot reliably resolve mDNS hostnames, especially after network disruptions.
            // Only add it if it actually responds: a dead entry here makes the server-select path
            // stall ~20s on the web client's startup probe (fetchWithTimeout: 20000ms).
            var localAddress = UrlHelper.NormalizeServerUrl(AppSettings.Default.JellyfinServerLocalAddress);
            if (!string.IsNullOrEmpty(localAddress) &&
                localAddress != serverUrl &&
                UrlHelper.IsValidHttpUrl(localAddress) &&
                !servers.Any(s => s?.GetValue<string>() == localAddress) &&
                await _apiClient.IsAddressReachableAsync(localAddress, System.TimeSpan.FromSeconds(3)))
            {
                servers.Add(localAddress);
                Trace.WriteLine($"[UpdateServerAddress] Added LocalAddress fallback: {localAddress}");
            }

            await File.WriteAllTextAsync(path, config.ToJsonString());

        }

        /// <summary>
        /// Injects auto-login credentials into the Jellyfin web app.
        /// This stores the access token and server info in localStorage format.
        /// Uses the real server ID from /System/Info/Public to prevent ServerMismatch errors.
        /// </summary>
        public async Task InjectAutoLoginAsync(PackageWorkspace ws)
        {
            var accessToken = AppSettings.Default.JellyfinAccessToken;
            var userId = AppSettings.Default.JellyfinUserId;
            var serverUrl = UrlHelper.NormalizeServerUrl(AppSettings.Default.JellyfinFullUrl);
            var serverId = AppSettings.Default.JellyfinServerId;
            var localAddress = AppSettings.Default.JellyfinServerLocalAddress;
            var serverName = AppSettings.Default.JellyfinServerName;

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(serverUrl))
            {
                Trace.WriteLine("[InjectAutoLogin] Missing credentials, skipping auto-login injection");
                return;
            }

            // If server ID or server name is not stored, try to fetch it now
            if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(serverName))
            {
                Trace.WriteLine("[InjectAutoLogin] Server ID/Name not cached, fetching from server...");
                var serverInfo = await _apiClient.GetPublicSystemInfoAsync(serverUrl);
                if (serverInfo != null && !string.IsNullOrEmpty(serverInfo.Id))
                {
                    serverId = serverInfo.Id;
                    localAddress = serverInfo.LocalAddress ?? "";
                    serverName = serverInfo.ServerName ?? "";
                    AppSettings.Default.JellyfinServerId = serverId;
                    AppSettings.Default.JellyfinServerLocalAddress = localAddress;
                    AppSettings.Default.JellyfinServerName = serverName;
                    AppSettings.Default.Save();
                    Trace.WriteLine($"[InjectAutoLogin] Fetched and stored server ID: {serverId}, Name: {serverName}");
                }
                else
                {
                    Trace.WriteLine("[InjectAutoLogin] WARNING: Could not fetch server ID, auto-login may fail with ServerMismatch");
                    return;
                }
            }

            string indexPath = Path.Combine(ws.Root, "www", "index.html");
            if (!File.Exists(indexPath))
            {
                Trace.WriteLine("[InjectAutoLogin] index.html not found");
                return;
            }

            var html = await File.ReadAllTextAsync(indexPath);

            // Only ship a LocalAddress if it's actually reachable AND distinct from the URL we
            // authenticated against. localAddress is the server's self-reported LAN IP from
            // /System/Info/Public and may be on a different subnet/VLAN, behind a reverse proxy,
            // or a Docker bridge IP. The web client probes each address with a 20s timeout on
            // startup, so an unreachable LocalAddress adds ~20s to every cold boot.
            bool localReachable = false;
            if (!string.IsNullOrEmpty(localAddress) &&
                UrlHelper.IsValidHttpUrl(localAddress) &&
                !string.Equals(UrlHelper.NormalizeServerUrl(localAddress), serverUrl, System.StringComparison.OrdinalIgnoreCase))
            {
                localReachable = await _apiClient.IsAddressReachableAsync(localAddress, System.TimeSpan.FromSeconds(3));
                Trace.WriteLine($"[InjectAutoLogin] LocalAddress {localAddress} reachable: {localReachable}");
            }

            // Create the credentials object that Jellyfin web expects
            // Using the REAL server ID (GUID) from /System/Info/Public to prevent ServerMismatch
            var credentialsScript = new StringBuilder();
            credentialsScript.AppendLine("<script>");
            credentialsScript.AppendLine("(function() {");
            credentialsScript.AppendLine("  try {");
            credentialsScript.AppendLine($"    var serverUrl = '{HtmlUtils.EscapeJsString(serverUrl)}';");
            credentialsScript.AppendLine($"    var serverId = '{HtmlUtils.EscapeJsString(serverId)}';");
            credentialsScript.AppendLine($"    var localAddress = '{HtmlUtils.EscapeJsString(localAddress)}';");
            credentialsScript.AppendLine($"    var serverName = '{HtmlUtils.EscapeJsString(serverName)}';");
            credentialsScript.AppendLine($"    var userId = '{HtmlUtils.EscapeJsString(userId)}';");
            credentialsScript.AppendLine($"    var accessToken = '{HtmlUtils.EscapeJsString(accessToken)}';");
            credentialsScript.AppendLine();
            credentialsScript.AppendLine("    // Create credentials object matching Jellyfin's expected format");
            credentialsScript.AppendLine("    // Using real server ID (GUID) from /System/Info/Public");
            credentialsScript.AppendLine("    var credentials = {");
            credentialsScript.AppendLine("      Servers: [{");
            credentialsScript.AppendLine("        Name: serverName || serverUrl,");
            credentialsScript.AppendLine("        ManualAddress: serverUrl,");
            // serverUrl is the address the installer just authenticated against -> known good.
            // Default LocalAddress to it too unless we verified a distinct LAN IP is reachable,
            // so the client can never stall 20s probing a dead address.
            credentialsScript.AppendLine(localReachable
                ? "        LocalAddress: localAddress || serverUrl,"
                : "        LocalAddress: serverUrl,");
            credentialsScript.AppendLine("        Id: serverId,");
            credentialsScript.AppendLine("        UserId: userId,");
            credentialsScript.AppendLine("        AccessToken: accessToken,");
            // ConnectionMode: Local=0, Remote=1, Manual=2. Point at the verified URL so the
            // web client tries it first and connects immediately instead of probing modes.
            credentialsScript.AppendLine("        LastConnectionMode: 2,");
            credentialsScript.AppendLine("        DateLastAccessed: new Date().getTime()");
            credentialsScript.AppendLine("      }]");
            credentialsScript.AppendLine("    };");
            credentialsScript.AppendLine();
            credentialsScript.AppendLine("    // Store in localStorage");
            credentialsScript.AppendLine("    localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));");
            credentialsScript.AppendLine();
            credentialsScript.AppendLine("    console.log('[Auto-Login] Credentials injected for server: ' + serverName + ' (' + serverUrl + ') with ID: ' + serverId);");
            credentialsScript.AppendLine("  } catch(e) {");
            credentialsScript.AppendLine("    console.error('[Auto-Login] Failed to inject credentials:', e);");
            credentialsScript.AppendLine("  }");
            credentialsScript.AppendLine("})();");
            credentialsScript.AppendLine("</script>");

            // Inject before </head> to ensure it runs before Jellyfin's scripts
            html = html.Replace("</head>", credentialsScript + "\n</head>");

            await File.WriteAllTextAsync(indexPath, html);
            Trace.WriteLine($"[InjectAutoLogin] Auto-login credentials injected successfully with server ID: {serverId}");
        }
    }
}