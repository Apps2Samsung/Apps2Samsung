using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public class AddLatestRelease
    {
        private readonly HttpClient _httpClient;

        public AddLatestRelease(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<List<GitHubRelease>> GetReleasesAsync(string url, string prefix, string displayName, int take = 1)
        {
            if (take < 1) take = 1;

            try
            {
                using var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                try
                {
                    var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(
                        json,
                        JsonSerializerOptionsProvider.Default);

                    if (releases == null || releases.Count == 0)
                        return new List<GitHubRelease>();

                    releases = releases
                        .Select(r =>
                        {
                            r.Assets = r.Assets?
                                .Where(a =>
                                    !string.IsNullOrWhiteSpace(a.FileName) &&
                                    (a.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) ||
                                     a.FileName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
                                .ToList() ?? new List<Asset>();

                            return r;
                        })
                        .Where(r => r.Assets.Count > 0)
                        .ToList();

                    if (releases.Count == 0)
                        return new List<GitHubRelease>();

                    var result = releases.Count > take ? releases.GetRange(0, take) : releases;

                    foreach (var r in result)
                    {
                        r.Name = string.IsNullOrWhiteSpace(displayName)
                            ? $"{prefix}{r.Name}"
                            : displayName;
                    }

                    return result;
                }
                catch (JsonException)
                {
                    var latest = JsonSerializer.Deserialize<GitHubRelease>(
                        json,
                        JsonSerializerOptionsProvider.Default);

                    if (latest == null)
                        return new List<GitHubRelease>();

                    latest.Assets = latest.Assets?
                        .Where(a =>
                            !string.IsNullOrWhiteSpace(a.FileName) &&
                            (a.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) ||
                             a.FileName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
                        .ToList() ?? new List<Asset>();

                    if (latest.Assets.Count == 0)
                        return new List<GitHubRelease>();

                    latest.Name = string.IsNullOrWhiteSpace(displayName)
                        ? $"{prefix}{latest.Name}"
                        : displayName;

                    return new List<GitHubRelease> { latest };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to fetch release from {url}: {ex}");
                return new List<GitHubRelease>();
            }
        }
    }
}