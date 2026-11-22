using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Certificate
{
    /// <summary>How a refresh of an authority set ended.</summary>
    public enum AuthorityRefresh
    {
        /// <summary>A newer set was downloaded, checked and is in use from now on.</summary>
        Replaced,
        /// <summary>The server has nothing that ends later than what is in use.</summary>
        NothingNewer,
        /// <summary>The download did not work (no network, a blocked or damaged file).</summary>
        Failed,
    }

    /// <summary>
    /// The certificate authorities in use (#1153): the ones shipped with the app, or a newer set
    /// downloaded after the user agreed to it. The shipped files are never changed; a downloaded
    /// set lives in its own folder next to the user's data and wins while it is complete.
    /// One per process: it also tells <see cref="TizenSdkAssets"/> where a downloaded Tizen
    /// authority is.
    /// </summary>
    public sealed class AuthorityStore
    {
        /// <summary>Roughly what the two downloads weigh, for the text that asks.</summary>
        public const int SamsungDownloadMb = 44;
        public const int TizenDownloadMb = 4;

        private readonly string _shippedSamsung;
        private readonly string _cacheSamsung;
        private readonly string _cacheTizen;

        // "Try anyway" for a set: not asked again until the app is closed.
        private readonly HashSet<AuthoritySet> _accepted = new();

        /// <param name="cacheRoot">A writable folder in the user's data; downloaded sets go under it.</param>
        /// <param name="shippedSamsungFolder">The folder with the three Samsung authority files the app ships.</param>
        public AuthorityStore(string cacheRoot, string shippedSamsungFolder)
        {
            _shippedSamsung = shippedSamsungFolder;
            _cacheSamsung = Path.Combine(cacheRoot, "samsung");
            _cacheTizen = Path.Combine(cacheRoot, "tizen");
            TizenSdkAssets.DeveloperCaOverrideDirectory = _cacheTizen;
        }

        /// <summary>The folder the certificate service reads the Samsung authorities from: the downloaded set when it is complete, else the shipped one.</summary>
        public string SamsungFolder =>
            CertificateAuthorities.SamsungFiles.All(f => File.Exists(Path.Combine(_cacheSamsung, f))) ? _cacheSamsung : _shippedSamsung;

        /// <summary>The authority certificates in use for <paramref name="set"/>, by file name. A file that is missing is left out.</summary>
        public IReadOnlyDictionary<string, byte[]> Current(AuthoritySet set)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (set == AuthoritySet.Samsung)
            {
                var folder = SamsungFolder;
                foreach (var name in CertificateAuthorities.SamsungFiles)
                {
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path))
                        files[name] = File.ReadAllBytes(path);
                }
            }
            else
            {
                using var stream = TizenSdkAssets.DeveloperCa();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                files[CertificateAuthorities.TizenDeveloperCa] = memory.ToArray();
            }
            return files;
        }

        /// <summary>
        /// The authority of <paramref name="set"/> that ended first, when one has; null while all are
        /// still running, and null after the user chose to try anyway in this session.
        /// </summary>
        public AuthorityFile? Ended(AuthoritySet set, DateTimeOffset now) =>
            _accepted.Contains(set) ? null : CertificateAuthorities.Ended(Current(set), now).FirstOrDefault();

        /// <summary>The user chose to go on with an ended set: it is not asked about again until the app is closed.</summary>
        public void AcceptEnded(AuthoritySet set) => _accepted.Add(set);

        /// <summary>
        /// Downloads the set the server has now and takes it into use when it is newer. Every file
        /// has to be a certificate that is valid now, at least one has to end later than the one it
        /// replaces, and a Tizen authority's key has to be the key of its certificate.
        /// </summary>
        public async Task<(AuthorityRefresh Result, string? Detail)> RefreshAsync(
            HttpClient http, AuthoritySet set, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            AuthorityDownload? download;
            try
            {
                download = await CertificateAuthorities.DownloadAsync(http, set, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Authorities] {set}: download failed: {ex.Message}");
                return (AuthorityRefresh.Failed, ex.Message);
            }

            if (download is null)
            {
                Trace.WriteLine($"[Authorities] {set}: the server named no usable archive.");
                return (AuthorityRefresh.Failed, null);
            }

            var current = Current(set);
            var certificates = download.Files.Where(f => !f.Key.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)).ToList();
            bool anyNewer = certificates.Any(f =>
                CertificateAuthorities.IsUsableReplacement(f.Value, current.TryGetValue(f.Key, out var have) ? have : null, now));
            bool allRunning = certificates.All(f => CertificateAuthorities.EndsAt(f.Value) is { } ends && ends > now);
            if (!anyNewer || !allRunning)
            {
                Trace.WriteLine($"[Authorities] {set}: nothing newer on the server.");
                return (AuthorityRefresh.NothingNewer, null);
            }

            if (set == AuthoritySet.Tizen && !TizenLocalCertificateService.IsDeveloperCaPair(
                    download.Files[CertificateAuthorities.TizenDeveloperCa], download.Files[CertificateAuthorities.TizenDeveloperCaKey]))
            {
                Trace.WriteLine("[Authorities] Tizen: the downloaded key does not open or is not the certificate's key.");
                return (AuthorityRefresh.Failed, null);
            }

            try
            {
                var folder = set == AuthoritySet.Samsung ? _cacheSamsung : _cacheTizen;
                Directory.CreateDirectory(folder);
                foreach (var file in download.Files)
                    await File.WriteAllBytesAsync(Path.Combine(folder, file.Key), file.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[Authorities] {set}: could not save the downloaded set: {ex.Message}");
                return (AuthorityRefresh.Failed, ex.Message);
            }

            Trace.WriteLine($"[Authorities] {set}: a newer set is in use.");
            return (AuthorityRefresh.Replaced, null);
        }
    }
}
