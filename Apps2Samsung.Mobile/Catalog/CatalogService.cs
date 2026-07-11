using System.Net.Http.Headers;
using System.Text.Json;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Catalog;

/// <summary>
/// Builds the App/Version catalog exactly like the desktop: loads the provider manifest
/// (remote → local cache → bundled fallback), then fetches each provider's GitHub releases and
/// flattens them into a sorted list of selectable releases. The GitHub PAT (if set in settings)
/// and a User-Agent are attached per-request for GitHub hosts to dodge API rate limits.
/// </summary>
public sealed class CatalogService
{
	private const string ManifestUrl =
		"https://raw.githubusercontent.com/Apps2Samsung/Apps2Samsung/main/third-party-apps.json";
	private const string BundledAsset = "third-party-apps.json";
	private const string CacheFileName = "third-party-apps.cache.json";

	private static readonly JsonSerializerOptions Json = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private readonly HttpClient _http;

	public CatalogService(HttpClient http) => _http = http;

	/// <summary>The catalog plus how many providers failed to load (e.g. GitHub rate limit).</summary>
	public sealed record CatalogResult(IReadOnlyList<GitHubRelease> Releases, int Failed, int Total);

	/// <summary>Loads the catalog and returns the selectable releases, sorted by name.</summary>
	public async Task<CatalogResult> LoadReleasesAsync()
	{
		var manifest = await GetManifestAsync();
		var providers = manifest.Providers.Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();

		// Fetch every provider concurrently — one slow/failing source no longer blocks the rest.
		var tasks = providers.Select(async provider =>
		{
			var take = provider.Take;
			if (take > 1 && !MobileSettings.ShowAllJellyfinVersions)
				take = 1; // collapse Jellyfin history to latest unless opted in

			var (releases, ok) = await GetReleasesAsync(provider.Url, provider.Prefix, provider.DisplayName, take);

			var entries = new List<GitHubRelease>();
			if (provider.ExpandAssets)
			{
				// One release entry per .wgt asset (Tizen Community bundle).
				foreach (var r in releases)
					foreach (var asset in r.Assets)
						entries.Add(new GitHubRelease
						{
							Name = Path.GetFileNameWithoutExtension(asset.FileName),
							TagName = r.TagName,
							PublishedAt = r.PublishedAt,
							Url = r.Url,
							Assets = new List<Asset> { asset },
						});
			}
			else
			{
				entries.AddRange(releases);
			}

			return (Entries: entries, Ok: ok);
		}).ToList();

		var results = await Task.WhenAll(tasks);
		var list = results.SelectMany(r => r.Entries).ToList();
		var failed = results.Count(r => !r.Ok);

		list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		return new CatalogResult(list, failed, providers.Count);
	}

	private async Task<ProviderManifest> GetManifestAsync()
	{
		var cachePath = Path.Combine(FileSystem.AppDataDirectory, CacheFileName);

		// 1) Remote (and refresh the cache).
		try
		{
			using var req = NewGet(ManifestUrl);
			using var resp = await _http.SendAsync(req);
			resp.EnsureSuccessStatusCode();
			var json = await resp.Content.ReadAsStringAsync();
			var manifest = JsonSerializer.Deserialize<ProviderManifest>(json, Json);
			if (manifest is { Providers.Count: > 0 })
			{
				try { File.WriteAllText(cachePath, json); } catch { /* cache is best-effort */ }
				return manifest;
			}
		}
		catch { /* fall through to cache/bundled */ }

		// 2) Last known good cache.
		try
		{
			if (File.Exists(cachePath))
			{
				var manifest = JsonSerializer.Deserialize<ProviderManifest>(File.ReadAllText(cachePath), Json);
				if (manifest is { Providers.Count: > 0 })
					return manifest;
			}
		}
		catch { /* fall through to bundled */ }

		// 3) Bundled fallback (always present).
		try
		{
			using var src = await FileSystem.OpenAppPackageFileAsync(BundledAsset);
			using var reader = new StreamReader(src);
			var manifest = JsonSerializer.Deserialize<ProviderManifest>(await reader.ReadToEndAsync(), Json);
			if (manifest is not null)
				return manifest;
		}
		catch { /* give up gracefully */ }

		return new ProviderManifest();
	}

	// Ported from the desktop AddLatestRelease: handles both the /releases (list) and
	// /releases/tags/<tag> (single) response shapes, keeps only .wgt/.tpk assets, truncates to
	// `take`, and applies the display name/prefix. Returns Ok=false when the HTTP call itself
	// failed (network / GitHub rate limit) so the caller can distinguish that from "no assets".
	private async Task<(List<GitHubRelease> Releases, bool Ok)> GetReleasesAsync(string url, string prefix, string displayName, int take)
	{
		if (take < 1) take = 1;

		try
		{
			using var req = NewGet(url);
			using var resp = await _http.SendAsync(req);
			resp.EnsureSuccessStatusCode();
			var json = await resp.Content.ReadAsStringAsync();

			List<GitHubRelease> releases;
			try
			{
				releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, Json) ?? new();
			}
			catch (JsonException)
			{
				var single = JsonSerializer.Deserialize<GitHubRelease>(json, Json);
				releases = single is null ? new() : new() { single };
			}

			foreach (var r in releases)
				r.Assets = r.Assets
					.Where(a => !string.IsNullOrWhiteSpace(a.FileName)
						&& (a.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase)
							|| a.FileName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
					.ToList();

			releases = releases.Where(r => r.Assets.Count > 0).ToList();
			if (releases.Count == 0)
				return (new(), true); // reached GitHub fine, just nothing installable

			var result = releases.Count > take ? releases.GetRange(0, take) : releases;
			foreach (var r in result)
				r.Name = string.IsNullOrWhiteSpace(displayName) ? $"{prefix}{r.Name}" : displayName;

			return (result, true);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"Failed to fetch releases from {url}: {ex}");
			return (new(), false);
		}
	}

	// A GET request carrying a User-Agent (GitHub rejects UA-less requests) and, for GitHub hosts
	// only, the PAT — never leaking the token to non-GitHub hosts on the shared HttpClient.
	private static HttpRequestMessage NewGet(string url)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.UserAgent.ParseAdd("Apps2Samsung-Mobile");

		var token = MobileSettings.GitHubToken;
		if (!string.IsNullOrWhiteSpace(token) && IsGitHubHost(new Uri(url).Host))
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		return req;
	}

	private static bool IsGitHubHost(string host) =>
		host.Contains("github", StringComparison.OrdinalIgnoreCase);
}
