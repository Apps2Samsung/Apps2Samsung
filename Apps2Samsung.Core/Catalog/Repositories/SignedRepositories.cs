using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog.Repositories
{
    /// <summary>How one signed repository came out of a catalog load.</summary>
    public enum RepositoryState
    {
        /// <summary>Fetched and verified now.</summary>
        Ok,
        /// <summary>Not verified now (offline, expired, bad file); listed from its last verified copy, nothing installable.</summary>
        Stale,
        /// <summary>The publisher's key changed: nothing is listed until the user approves the new fingerprint.</summary>
        KeyChanged,
        /// <summary>Not verified now and no verified copy on this device: nothing listed.</summary>
        Failed,
    }

    /// <summary>One repository's load: its state, why, and the providers it adds to the catalog.</summary>
    public sealed record RepositoryLoad(
        UserSource Source,
        RepositoryState State,
        string? Reason,
        string? Message,
        RepositoryManifest? Manifest,
        IReadOnlyList<ProviderEntry> Providers,
        string? NewFingerprint = null,
        string? NewPublicKey = null);

    /// <summary>What <see cref="SignedRepositories.ProbeAsync"/> found at a URL, for the approval screen.</summary>
    public sealed record RepositoryProbe(
        bool Ok,
        string? Reason,
        string? Message,
        RepositoryManifest? Manifest,
        string? Fingerprint,
        string? PublicKey,
        string Domain)
    {
        /// <summary>The source to store once the user approved this key; only for an <see cref="Ok"/> probe.</summary>
        public UserSource Approve(string url, string? name = null) => new()
        {
            Url = url.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? Manifest!.Repository.Name : name.Trim(),
            RepositoryId = Manifest!.Repository.Id,
            Fingerprint = Fingerprint,
            PublicKey = PublicKey,
            AcceptedGeneratedAt = Manifest.GeneratedAt,
        };
    }

    /// <summary>
    /// Independent signed repositories (#1018): adding one (<see cref="ProbeAsync"/>, then the user
    /// approves the key fingerprint) and reading one on every catalog load (<see cref="LoadAsync"/>).
    /// The spec is tizen-community-packages <c>docs/repositories.md</c>.
    /// <para>
    /// A load verifies against the key stored at approval, never a newly fetched one, and fetches
    /// <c>repository.pem</c> only to notice that the publisher changed it: then the repository stops
    /// loading until the user approves the new fingerprint. The last verified file is kept per
    /// repository under the cache folder; when a load fails it is listed from that copy with
    /// <see cref="CatalogSource.InstallBlockedReason"/> set, and its <c>generatedAt</c> is the floor a
    /// newer file must not go below (rollback).
    /// </para>
    /// </summary>
    public sealed class SignedRepositories
    {
        public const string KeyFileName = "repository.pem";

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RepositoryLoad> Loads = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The last load of the repository at <paramref name="url"/> in this process, for its status in
        /// the source list (Settings > Your sources); null before the catalog loaded it once.
        /// </summary>
        public static RepositoryLoad? LastLoad(string url) => Loads.TryGetValue(url.Trim(), out var load) ? load : null;

        private readonly HttpClient _http;
        private readonly string? _cacheDir;
        private readonly Func<DateTimeOffset> _clock;

        /// <param name="cacheDir">Where the last verified copies go; null keeps none (a failed load then lists nothing).</param>
        public SignedRepositories(HttpClient http, string? cacheDir, Func<DateTimeOffset>? clock = null)
        {
            _http = http;
            _cacheDir = cacheDir;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>The key's URL: <c>repository.pem</c> in the same folder as the signed manifest.</summary>
        public static Uri KeyUrl(string manifestUrl) => new(new Uri(manifestUrl), KeyFileName);

        /// <summary>
        /// Reads a repository the user wants to add: the manifest and the key next to it, over
        /// validated HTTPS, checked against that key. Nothing is stored: the caller shows name,
        /// domain, maintainer and fingerprint and stores <see cref="RepositoryProbe.Approve"/> on yes.
        /// </summary>
        public async Task<RepositoryProbe> ProbeAsync(string url, CancellationToken ct = default)
        {
            var domain = Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) ? u.Host : string.Empty;
            if (!UserSources.IsSignedRepository(url))
                return new RepositoryProbe(false, "url", "not an HTTPS link to a repository.json.signed file", null, null, null, domain);
            byte[] file;
            string key;
            try
            {
                file = await GetBytesAsync(url!.Trim(), RepositoryVerifier.MaxFileBytes, ct).ConfigureAwait(false);
                key = Encoding.ASCII.GetString(await GetBytesAsync(KeyUrl(url.Trim()).AbsoluteUri, 64 * 1024, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new RepositoryProbe(false, "fetch", ex.Message, null, null, null, domain);
            }
            var result = RepositoryVerifier.Verify(file, key, _clock());
            return new RepositoryProbe(result.Ok, result.Reason, result.Message, result.Manifest, result.Fingerprint ?? RepositoryVerifier.Fingerprint(key), key, domain);
        }

        /// <summary>One approved repository for a catalog load. Never throws for a broken or unreachable repository.</summary>
        public async Task<RepositoryLoad> LoadAsync(UserSource source, CancellationToken ct = default)
        {
            var load = await LoadCoreAsync(source, ct).ConfigureAwait(false);
            Loads[source.Url.Trim()] = load;
            return load;
        }

        private async Task<RepositoryLoad> LoadCoreAsync(UserSource source, CancellationToken ct)
        {
            var cached = ReadCache(source);
            var floor = Max(source.AcceptedGeneratedAt, cached?.GeneratedAt);

            byte[]? file = null;
            string? fetchError = null;
            try
            {
                file = await GetBytesAsync(source.Url, RepositoryVerifier.MaxFileBytes, ct).ConfigureAwait(false);
                // The key is fetched only to notice a change; it is never used to verify.
                var key = Encoding.ASCII.GetString(await GetBytesAsync(KeyUrl(source.Url).AbsoluteUri, 64 * 1024, ct).ConfigureAwait(false));
                var fingerprint = RepositoryVerifier.Fingerprint(key);
                if (fingerprint is not null && fingerprint != RepositoryVerifier.NormalizeFingerprint(source.Fingerprint!))
                    return new RepositoryLoad(source, RepositoryState.KeyChanged, RepositoryVerifier.Reasons.Fingerprint,
                        $"the publisher's key changed to {fingerprint}; approve it to use this repository again", null, Array.Empty<ProviderEntry>(), fingerprint, key);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                fetchError = ex.Message;
            }

            if (file is not null)
            {
                var result = RepositoryVerifier.Verify(file, source.PublicKey!, _clock(), source.Fingerprint, floor, source.RepositoryId);
                if (result.Ok)
                {
                    WriteCache(source, file);
                    return new RepositoryLoad(source, RepositoryState.Ok, null, null, result.Manifest, ToProviders(source, result.Manifest!, null));
                }
                fetchError = $"[{result.Reason}] {result.Message}";
            }

            Trace.WriteLine($"[Repositories] {source.Url}: {fetchError}");
            if (cached is null)
                return new RepositoryLoad(source, RepositoryState.Failed, file is null ? "fetch" : null, fetchError, null, Array.Empty<ProviderEntry>());
            var blocked = $"{source.Name} could not be verified just now ({fetchError}); listed from the copy verified {cached.GeneratedAt:yyyy-MM-dd}, not installable";
            return new RepositoryLoad(source, RepositoryState.Stale, file is null ? "fetch" : null, fetchError, cached, ToProviders(source, cached, blocked));
        }

        /// <summary>The manifest's apps as catalog providers, with their verified releases and this repository as their source.</summary>
        public static List<ProviderEntry> ToProviders(UserSource source, RepositoryManifest manifest, string? installBlockedReason)
        {
            var label = new CatalogSource(CatalogSourceKind.Repository, manifest.Repository.Name, manifest.Repository.Id, installBlockedReason, source.Url.Trim());
            var prefix = UserSources.Id(source);
            var providers = new List<ProviderEntry>();
            foreach (var p in manifest.Providers)
            {
                var releases = p.Releases.Where(r => !r.Draft && r.Assets.Count > 0).ToList();
                if (releases.Count == 0)
                    continue;
                foreach (var release in releases)
                {
                    if (string.IsNullOrWhiteSpace(release.Name))
                        release.Name = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName!;
                    release.IsAdult = p.Adult;
                    release.IsRoot = HasFlag(p, "root");
                    // #1016's labels: per file too, so "Show closed-source apps" off drops them like the catalog's own.
                    release.IsClosedSource = HasFlag(p, "closed-source");
                    release.IsSelfUpdating = HasFlag(p, "self-updating");
                    foreach (var asset in release.Assets)
                    {
                        asset.IsClosedSource = release.IsClosedSource;
                        asset.IsSelfUpdating = release.IsSelfUpdating;
                    }
                }
                providers.Add(new ProviderEntry
                {
                    // The user- prefix keeps the snapshot and the download counts off it, as for every user source.
                    Id = $"{prefix}-{p.Id}",
                    Url = p.Url,
                    DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName!,
                    Prefix = p.Prefix ?? string.Empty,
                    Take = Math.Max(1, p.Take),
                    Category = AppCategories.Clean(p.Category),
                    CertLevel = p.CertLevel ?? string.Empty,
                    BuildInfo = p.BuildInfo,
                    IsUserSource = true,
                    Source = label,
                    Releases = releases,
                });
            }
            return providers;
        }

        private static bool HasFlag(RepositoryProvider provider, string flag) =>
            provider.Flags.Any(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));

        /// <summary>Why <paramref name="release"/> cannot be installed now (a repository listed from its last verified copy), or null; en.json text.</summary>
        public static string? InstallBlocked(GitHubRelease release, Func<string, string> localize) =>
            release.Source.CanInstall ? null : string.Format(localize("errRepoInstallBlocked"), release.Source.Name);

        /// <summary>
        /// After the download: a signed repository's file must hash to the digest it signed for, or
        /// nothing is installed. Null when it does, or when the file is not from a repository.
        /// </summary>
        public static string? DigestProblem(GitHubRelease release, Asset asset, string path, Func<string, string> localize) =>
            release.Source.Kind != CatalogSourceKind.Repository
            || (!string.IsNullOrWhiteSpace(asset.Digest) && DigestMatches(path, asset.Digest))
                ? null
                : string.Format(localize("errRepoDigest"), asset.FileName, release.Source.Name);

        /// <summary>The SHA-256 a downloaded package must have, from <see cref="Asset.Digest"/>: true when it matches.</summary>
        public static bool DigestMatches(string path, string digest)
        {
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return false;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(hash, digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase);
        }

        // ---- the last verified copy ----

        private string? CachePath(UserSource source) =>
            _cacheDir is null || string.IsNullOrWhiteSpace(source.RepositoryId) ? null : Path.Combine(_cacheDir, source.RepositoryId + ".signed");

        // Re-verified with the stored key on every read; its age does not matter here (Stale says how old it is).
        private RepositoryManifest? ReadCache(UserSource source)
        {
            var path = CachePath(source);
            if (path is null || !File.Exists(path))
                return null;
            try
            {
                var file = File.ReadAllBytes(path);
                var manifest = RepositoryVerifier.Verify(file, source.PublicKey!, _clock(), source.Fingerprint, null, source.RepositoryId, checkAge: false);
                return manifest.Ok ? manifest.Manifest : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void WriteCache(UserSource source, byte[] file)
        {
            var path = CachePath(source);
            if (path is null)
                return;
            try
            {
                Directory.CreateDirectory(_cacheDir!);
                File.WriteAllBytes(path, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[Repositories] could not keep {path}: {ex.Message}");
            }
        }

        private async Task<byte[]> GetBytesAsync(string url, int limit, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Apps2Samsung");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit)
                throw new IOException($"{url} is over {limit} bytes");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new IOException($"{url} is over {limit} bytes");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
            a is null ? b : b is null ? a : (a > b ? a : b);
    }
}
