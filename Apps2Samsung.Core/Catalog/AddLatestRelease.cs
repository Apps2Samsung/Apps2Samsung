using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public class AddLatestRelease
    {
        private readonly HttpClient _httpClient;
        private readonly Func<string?>? _tokenProvider;

        /// <param name="tokenProvider">
        /// Optional GitHub PAT source. The desktop leaves this null (its HttpClient handler injects
        /// the token); the mobile head passes its settings token so API calls dodge rate limits.
        /// </param>
        public AddLatestRelease(HttpClient httpClient, Func<string?>? tokenProvider = null)
        {
            _httpClient = httpClient;
            _tokenProvider = tokenProvider;
        }

        // GitHub rejects UA-less requests; attach a User-Agent (and the PAT for GitHub hosts).
        private HttpRequestMessage BuildRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Apps2Samsung");

            var token = _tokenProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(token) &&
                new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return request;
        }

        /// <summary>Fetches releases, discarding failure status (empty list on any error).</summary>
        public async Task<List<GitHubRelease>> GetReleasesAsync(string url, string prefix, string displayName, int take = 1)
            => (await GetReleasesWithStatusAsync(url, prefix, displayName, take)).Releases;

        /// <summary>
        /// Fetches releases and reports whether the HTTP call succeeded. Handles both the /releases
        /// (list) and /releases/tags/&lt;tag&gt; (single) shapes, keeps only .wgt/.tpk assets,
        /// truncates to <paramref name="take"/>, and applies the display name/prefix.
        /// <c>Ok=false</c> means the request failed (network / GitHub rate limit), which the caller
        /// can distinguish from a successful fetch that simply had no installable assets.
        /// </summary>
        public async Task<(List<GitHubRelease> Releases, bool Ok)> GetReleasesWithStatusAsync(
            string url, string prefix, string displayName, int take = 1)
        {
            if (take < 1) take = 1;

            try
            {
                using var request = BuildRequest(url);
                using var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                List<GitHubRelease> releases;
                try
                {
                    releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonSerializerOptionsProvider.Default)
                        ?? new List<GitHubRelease>();
                }
                catch (JsonException)
                {
                    var single = JsonSerializer.Deserialize<GitHubRelease>(json, JsonSerializerOptionsProvider.Default);
                    releases = single is null ? new List<GitHubRelease>() : new List<GitHubRelease> { single };
                }

                foreach (var r in releases)
                    r.Assets = r.Assets?
                        .Where(a => !string.IsNullOrWhiteSpace(a.FileName) &&
                            (a.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) ||
                             a.FileName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
                        .ToList() ?? new List<Asset>();

                releases = releases.Where(r => r.Assets.Count > 0).ToList();
                if (releases.Count == 0)
                    return (new List<GitHubRelease>(), true); // reached GitHub fine, just nothing installable

                var result = releases.Count > take ? releases.GetRange(0, take) : releases;
                foreach (var r in result)
                    r.Name = string.IsNullOrWhiteSpace(displayName) ? $"{prefix}{r.Name}" : displayName;

                return (result, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to fetch release from {url}: {ex}");
                return (new List<GitHubRelease>(), false);
            }
        }
    }
}