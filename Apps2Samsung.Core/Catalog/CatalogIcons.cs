using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The app icons the catalog names (<see cref="Asset.Icon"/> / <see cref="Asset.IconDigest"/>,
    /// #1000), as local files for the heads to show. An icon is downloaded once, kept only when its
    /// bytes hash to <c>icon_digest</c> and are a PNG, JPEG, GIF or WebP, and cached on disk under
    /// that hash: the same three rules TV-Store applies, so the three readers agree on what is shown.
    /// A head that gets null keeps its letter tile. Never throws; concurrent callers share one
    /// download per icon.
    /// </summary>
    public sealed class CatalogIcons
    {
        private static readonly Regex DigestPattern = new("^sha256:([0-9a-fA-F]{64})$", RegexOptions.Compiled);
        private static readonly string[] Extensions = { "png", "jpg", "gif", "webp" };
        /// <summary>TV-Store's cap; the snapshot publishes nothing bigger either.</summary>
        public const int MaxBytes = 1024 * 1024;
        /// <summary>How long a failed download is remembered before the icon is asked for again.</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

        private readonly HttpClient _http;
        private readonly string _directory;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _failedAt = new(StringComparer.Ordinal);

        /// <param name="http">Any client; apps2samsung.com needs no token.</param>
        /// <param name="directory">Where the icons are kept, <c>&lt;hex&gt;.&lt;ext&gt;</c> each; created on first use.</param>
        /// <param name="clock">Override for tests.</param>
        public CatalogIcons(HttpClient http, string directory, Func<DateTimeOffset>? clock = null)
        {
            _http = http;
            _directory = directory;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public string Directory => _directory;

        /// <summary>
        /// The asset whose icon stands for a release in a list: the default build's when it has
        /// one, else the first build with one. Null when no build names an icon.
        /// </summary>
        public static Asset? IconAssetOf(GitHubRelease? release)
        {
            if (release?.Assets is not { Count: > 0 } assets)
                return null;
            return assets.FirstOrDefault(a => a.IsDefault && Hex(a) is not null)
                ?? assets.FirstOrDefault(a => Hex(a) is not null);
        }

        /// <summary>
        /// The lower-case hex SHA-256 an asset's icon fields name, or null when the asset has no icon
        /// or the fields are not what the snapshot writes (an https URL and <c>sha256:&lt;64 hex&gt;</c>).
        /// </summary>
        public static string? Hex(Asset? asset)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.Icon) || string.IsNullOrWhiteSpace(asset.IconDigest))
                return null;
            if (!Uri.TryCreate(asset.Icon, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                return null;
            var match = DigestPattern.Match(asset.IconDigest);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>The local file for the release's icon (<see cref="IconAssetOf"/>), or null when there is none to show.</summary>
        public Task<string?> PathForAsync(GitHubRelease? release) => PathForAsync(IconAssetOf(release));

        /// <summary>
        /// The local file for an asset's icon, or null: no icon, a malformed field, a download that
        /// failed, bytes that do not hash to <c>icon_digest</c>, or not an image. A cached file is
        /// hashed again before it is handed out, so a damaged cache is replaced, never shown.
        /// </summary>
        public Task<string?> PathForAsync(Asset? asset)
        {
            var hex = Hex(asset);
            if (hex is null)
                return Task.FromResult<string?>(null);
            if (_paths.TryGetValue(hex, out var path) && File.Exists(path))
                return Task.FromResult<string?>(path);
            if (_failedAt.TryGetValue(hex, out var failedAt) && _clock() - failedAt < RetryAfter)
                return Task.FromResult<string?>(null);
            // One resolve per hash however many rows ask at once. The entry is removed by whoever
            // awaits it, not by the resolve itself: a resolve that completes synchronously (a cached
            // file) would otherwise run its clean-up before GetOrAdd had stored it.
            var task = _inFlight.GetOrAdd(hex, _ => ResolveAsync(hex, asset!.Icon!));
            return ForgetWhenDoneAsync(hex, task);
        }

        private async Task<string?> ForgetWhenDoneAsync(string hex, Task<string?> task)
        {
            try
            {
                return await task;
            }
            finally
            {
                _inFlight.TryRemove(new System.Collections.Generic.KeyValuePair<string, Task<string?>>(hex, task));
            }
        }

        private async Task<string?> ResolveAsync(string hex, string url)
        {
            var path = await CachedAsync(hex) ?? await DownloadAsync(hex, url);
            if (path is null)
                _failedAt[hex] = _clock();
            else
            {
                _paths[hex] = path;
                _failedAt.TryRemove(hex, out _);
            }
            return path;
        }

        private async Task<string?> CachedAsync(string hex)
        {
            foreach (var extension in Extensions)
            {
                var path = Path.Combine(_directory, $"{hex}.{extension}");
                if (!File.Exists(path))
                    continue;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    if (Sha256(bytes) == hex && WgtManifest.ImageExtension(bytes) is not null)
                        return path;
                    Trace.WriteLine($"[CatalogIcons] dropping a cached icon that does not hash to its name ({hex[..12]})");
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[CatalogIcons] cannot read {path}: {ex.Message}");
                }
            }
            return null;
        }

        private async Task<string?> DownloadAsync(string hex, string url)
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                if (bytes.Length > MaxBytes)
                {
                    Trace.WriteLine($"[CatalogIcons] {url} is {bytes.Length} bytes, more than {MaxBytes}; not showing it");
                    return null;
                }
                if (Sha256(bytes) != hex)
                {
                    Trace.WriteLine($"[CatalogIcons] {url} does not hash to the catalog's icon_digest; not showing it");
                    return null;
                }
                var extension = WgtManifest.ImageExtension(bytes);
                if (extension is null)
                {
                    Trace.WriteLine($"[CatalogIcons] {url} is not a PNG, JPEG, GIF or WebP; not showing it");
                    return null;
                }
                System.IO.Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"{hex}.{extension}");
                var part = path + ".part";
                await File.WriteAllBytesAsync(part, bytes);
                File.Move(part, path, overwrite: true);
                return path;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CatalogIcons] {url} failed: {ex.Message}");
                return null;
            }
        }

        private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
