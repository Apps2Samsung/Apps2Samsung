using Jellyfin2Samsung.Helpers.Core;
using Jellyfin2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin2Samsung.Helpers.API
{
    public class JellyfinApiClient
    {
        private readonly HttpClient _httpClient;

        public JellyfinApiClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        /// <summary>
        /// Checks if a valid Jellyfin configuration exists with an authenticated user.
        /// Uses AccessToken from username/password authentication.
        /// </summary>
        public static bool IsValidJellyfinConfiguration()
        {
            return !string.IsNullOrEmpty(AppSettings.Default.JellyfinFullUrl) &&
                   !string.IsNullOrEmpty(AppSettings.Default.JellyfinAccessToken) &&
                   !string.IsNullOrEmpty(AppSettings.Default.JellyfinUserId) &&
                   UrlHelper.IsValidHttpUrl($"{AppSettings.Default.JellyfinFullUrl}/Users");
        }

        /// <summary>
        /// Checks if the user has a valid authentication (AccessToken + UserId).
        /// </summary>
        public static bool HasValidAuthentication()
        {
            return !string.IsNullOrEmpty(AppSettings.Default.JellyfinAccessToken) &&
                   !string.IsNullOrEmpty(AppSettings.Default.JellyfinUserId);
        }

        public async Task<List<JellyfinPluginInfo>> GetInstalledPluginsAsync(string serverUrl)
        {
            var list = new List<JellyfinPluginInfo>();
            try
            {
                string url = UrlHelper.CombineUrl(serverUrl, "/Plugins");
                Trace.WriteLine("Fetching installed plugins from: " + url);
                var json = await _httpClient.GetStringAsync(url);
                var parsed = JsonSerializer.Deserialize<List<JellyfinPluginInfo>>(json, JsonSerializerOptionsProvider.Default);

                if (parsed != null)
                    list.AddRange(parsed);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Failed to fetch /Plugins: " + ex);
            }

            return list;
        }

        public async Task<JellyfinPublicSystemInfo?> GetPublicSystemInfoAsync(string serverUrl)
        {
            try
            {
                string url = UrlHelper.CombineUrl(serverUrl, "/System/Info/Public");
                Trace.WriteLine("Fetching Jellyfin public system info from: " + url);

                var json = await _httpClient.GetStringAsync(url);
                return JsonSerializer.Deserialize<JellyfinPublicSystemInfo>(json, JsonSerializerOptionsProvider.Default);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Failed to fetch /System/Info/Public: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Quick reachability probe used before baking an address into auto-login credentials.
        /// The Jellyfin web client probes each stored server address with a 20s timeout on
        /// startup, so any address we inject that the TV can't actually reach adds ~20s to
        /// every cold boot. We only ship addresses that answer within a short budget.
        /// </summary>
        public async Task<bool> IsAddressReachableAsync(string serverUrl, TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                string url = UrlHelper.CombineUrl(serverUrl, "/System/Info/Public");
                using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets up HTTP headers for authenticated Jellyfin API requests.
        /// Uses the AccessToken obtained from username/password authentication.
        /// </summary>
        private void SetupHeaders()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.Api.UserAgent);
            _httpClient.DefaultRequestHeaders.Add("Authorization",
                string.Format(Constants.Api.MediaBrowserAuthHeader, AppSettings.Default.JellyfinAccessToken));
        }

        /// <summary>
        /// Authenticates with Jellyfin using username and password.
        /// Returns the access token, user ID, and admin status on success.
        /// </summary>
        public async Task<(string? accessToken, string? userId, bool isAdmin, string? error)> AuthenticateAsync(string username, string password)
        {
            try
            {
                var serverUrl = UrlHelper.NormalizeServerUrl(AppSettings.Default.JellyfinFullUrl);
                var authUrl = $"{serverUrl}/Users/AuthenticateByName";

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("X-Emby-Authorization", Constants.Api.EmbyAuthHeader);

                var authPayload = new
                {
                    Username = username,
                    Pw = password
                };

                var json = JsonSerializer.Serialize(authPayload);
                using var content = new StringContent(json, Encoding.UTF8, Constants.Api.JsonContentType);

                var response = await _httpClient.PostAsync(authUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    var authResponse = JsonNode.Parse(responseJson);

                    var accessToken = authResponse?["AccessToken"]?.GetValue<string>();
                    var userId = authResponse?["User"]?["Id"]?.GetValue<string>();
                    var isAdmin = authResponse?["User"]?["Policy"]?["IsAdministrator"]?.GetValue<bool>() ?? false;

                    Trace.WriteLine($"[Auth] User authenticated. IsAdmin: {isAdmin}");

                    return (accessToken, userId, isAdmin, null);
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Trace.WriteLine($"Authentication failed: {response.StatusCode} - {errorContent}");
                    return (null, null, false, $"Authentication failed: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Authentication error: {ex}");
                return (null, null, false, ex.Message);
            }
        }

        /// <summary>
        /// Loads all Jellyfin users. Requires admin authentication.
        /// </summary>
        public async Task<List<JellyfinUser>> LoadUsersAsync()
        {
            var users = new List<JellyfinUser>();
            try
            {
                SetupHeaders();
                var serverUrl = UrlHelper.NormalizeServerUrl(AppSettings.Default.JellyfinFullUrl);
                var usersUrl = $"{serverUrl}/Users";

                Trace.WriteLine($"[LoadUsers] Fetching users from: {usersUrl}");

                var response = await _httpClient.GetAsync(usersUrl);
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    var usersArray = JsonNode.Parse(responseJson)?.AsArray();

                    if (usersArray != null)
                    {
                        foreach (var userNode in usersArray)
                        {
                            var user = new JellyfinUser
                            {
                                Id = userNode?["Id"]?.GetValue<string>() ?? "",
                                Name = userNode?["Name"]?.GetValue<string>() ?? ""
                            };

                            if (!string.IsNullOrEmpty(user.Id) && !string.IsNullOrEmpty(user.Name))
                            {
                                users.Add(user);
                            }
                        }

                        Trace.WriteLine($"[LoadUsers] Loaded {users.Count} users");
                    }
                }
                else
                {
                    Trace.WriteLine($"[LoadUsers] Failed to load users: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LoadUsers] Error loading users: {ex}");
            }

            return users;
        }

        /// <summary>
        /// Tests if the current server URL is reachable by checking the parameter url endpoint.
        /// </summary>
        public async Task<bool> TestServerConnectionAsync(string testUrl)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Clear();
                var response = await _httpClient.GetAsync(testUrl);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}
