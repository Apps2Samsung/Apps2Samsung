using System.Net.Http.Headers;
using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Catalog;

/// <summary>
/// Builds the App/Version catalog, mirroring the desktop. Loads the provider manifest
/// (remote → local cache → bundled fallback), then fetches each provider's GitHub releases via the
/// shared Core <see cref="AddLatestRelease"/> (which carries a User-Agent + the settings PAT to
/// dodge rate limits) and flattens them into a sorted list of selectable releases. Models and the
/// fetcher live in Apps2Samsung.Core — this class is just the mobile-side loader/orchestration.
/// </summary>
public sealed class CatalogService
{
	private const string ManifestUrl =
		"https://raw.githubusercontent.com/Apps2Samsung/Apps2Samsung/main/third-party-apps.json";
	private const string BundledAsset = "third-party-apps.json";
	private const string CacheFileName = "third-party-apps.cache.json";

	private readonly HttpClient _http;
	private readonly AddLatestRelease _releases;
	private readonly BuildInfoService _buildInfo;

	public CatalogService(HttpClient http)
	{
		_http = http;
		// Mobile has no auth handler on its HttpClient, so supply the PAT here.
		_releases = new AddLatestRelease(http, () => MobileSettings.GitHubToken);
		_buildInfo = new BuildInfoService(http, () => MobileSettings.GitHubToken);
	}

	/// <summary>Loads the "app preview + versions" catalog for the info view (the "?" button).</summary>
	public async Task<BuildInfoResult> LoadBuildInfoAsync()
	{
		var manifest = await GetManifestAsync();
		return await _buildInfo.LoadAsync(manifest);
	}

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

			var (releases, ok) = await _releases.GetReleasesWithStatusAsync(
				provider.Url, provider.Prefix, provider.DisplayName, take);

			// Apps declaring cert_level: partner auto-request Partner signing at install.
			var requiresPartner = string.Equals(provider.CertLevel, "partner", StringComparison.OrdinalIgnoreCase);

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
							RequiresPartner = requiresPartner,
						});
			}
			else
			{
				foreach (var r in releases)
					r.RequiresPartner = requiresPartner;
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
			var manifest = JsonSerializer.Deserialize<ProviderManifest>(json, JsonSerializerOptionsProvider.Default);
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
				var manifest = JsonSerializer.Deserialize<ProviderManifest>(
					File.ReadAllText(cachePath), JsonSerializerOptionsProvider.Default);
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
			var manifest = JsonSerializer.Deserialize<ProviderManifest>(
				await reader.ReadToEndAsync(), JsonSerializerOptionsProvider.Default);
			if (manifest is not null)
				return manifest;
		}
		catch { /* give up gracefully */ }

		return new ProviderManifest();
	}

	// A GET carrying a User-Agent and, for GitHub hosts, the PAT.
	private static HttpRequestMessage NewGet(string url)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.UserAgent.ParseAdd("Apps2Samsung");

		var token = MobileSettings.GitHubToken;
		if (!string.IsNullOrWhiteSpace(token) && new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		return req;
	}
}
