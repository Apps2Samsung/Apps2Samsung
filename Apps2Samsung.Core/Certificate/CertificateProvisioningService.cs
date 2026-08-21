using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;

namespace Apps2Samsung.Certificate
{
    /// <summary>A signing profile ensured for an install.</summary>
    public sealed record CertificateProfile(
        string AuthorP12,
        string DistributorP12,
        string Password,
        string ProfileDir,
        string ProfileName,
        CertificatePrivilegeLevel Level);

    /// <summary>
    /// Provisions and — crucially — <b>reuses</b> Samsung signing profiles, shared by both heads.
    /// Reuse rules (previously desktop-only, which is why the mobile head regenerated a cert on every
    /// install):
    /// <list type="bullet">
    /// <item>A valid author cert whose distributor already covers the TV's DUID (and any manual DUIDs)
    /// is reused with <b>no Samsung login</b>.</item>
    /// <item>If the author exists but the DUID isn't covered, only the distributor is regenerated
    /// (author keypair kept, so already-installed apps stay overwritable).</item>
    /// <item>Otherwise a full profile is minted.</item>
    /// </list>
    /// Public and Partner live in separate <c>"Jelly2Sams - Public/Partner"</c> folders so switching
    /// level never clobbers the other. A Samsung login (<see cref="ISamsungLoginService"/>) is only
    /// triggered when a regenerate/full-profile is actually needed.
    /// </summary>
    public sealed class CertificateProvisioningService
    {
        public const string ProfileBaseName = "Jelly2Sams";
        private const string AuthorFileName = "author.p12";
        private const string DistributorFileName = "distributor.p12";
        private const string PasswordFileName = "password.txt";
        private const int MaxDistributorDuids = 10;

        private readonly ITizenCertificateService _certService;
        private readonly ISamsungLoginService _login;

        public CertificateProvisioningService(ITizenCertificateService certService, ISamsungLoginService login)
        {
            _certService = certService;
            _login = login;
        }

        /// <summary>The profile folder name for a level, e.g. "Jelly2Sams - Partner".</summary>
        public static string ProfileName(CertificatePrivilegeLevel level) =>
            $"{ProfileBaseName} - {(level == CertificatePrivilegeLevel.Partner ? "Partner" : "Public")}";

        /// <summary>
        /// Ensure a signing profile for <paramref name="level"/> that covers <paramref name="deviceDuid"/>
        /// (+ <paramref name="manualDuids"/>), reusing an existing one when possible. Only performs a
        /// Samsung login when a full profile or a distributor regen is required.
        /// </summary>
        /// <param name="storePath">Root dir holding the level profiles (e.g. IAppConfig.CertificateStorePath).</param>
        /// <param name="caPath">Dir containing the Samsung CA files.</param>
        /// <param name="onLoginStarting">Invoked right before a Samsung login is triggered (heads use it
        /// to switch UI); not called on the silent-reuse path.</param>
        public async Task<CertificateProfile> EnsureAsync(
            string deviceDuid,
            CertificatePrivilegeLevel level,
            string storePath,
            string caPath,
            IEnumerable<string>? manualDuids = null,
            bool forceLogin = false,
            Action? onLoginStarting = null,
            ProgressCallback? progress = null,
            CancellationToken ct = default)
        {
            if (!TizenDuid.IsValid(deviceDuid))
                throw new ArgumentException("A valid TV DUID is required.", nameof(deviceDuid));

            var profileName = ProfileName(level);
            var profileDir = Path.Combine(storePath, profileName);

            var manual = (manualDuids ?? Enumerable.Empty<string>())
                .Where(TizenDuid.IsValid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool hasAuthor = HasUsableAuthorCert(profileDir);
            var covered = hasAuthor
                ? GetCoveredDuids(profileDir)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool duidsCovered = covered.Contains(deviceDuid) && manual.All(covered.Contains);

            bool needsFullProfile = forceLogin || !hasAuthor;
            bool needsDistributorOnly = !needsFullProfile && !duidsCovered;

            // ---- Silent reuse: valid author + DUID already covered ----
            if (!needsFullProfile && !needsDistributorOnly)
            {
                var pw = File.ReadAllText(Path.Combine(profileDir, PasswordFileName)).Trim();
                return new CertificateProfile(
                    Path.Combine(profileDir, AuthorFileName),
                    Path.Combine(profileDir, DistributorFileName),
                    pw, profileDir, profileName, level);
            }

            // ---- A Samsung login is required ----
            onLoginStarting?.Invoke();
            var auth = await _login.LoginAsync(ct);
            if (string.IsNullOrEmpty(auth.access_token))
                throw new InvalidOperationException("Failed to authenticate with the Samsung account.");

            var duids = BuildDistributorDuids(deviceDuid, manual, covered);

            string authorP12, distributorP12, password;
            if (needsFullProfile)
            {
                (authorP12, distributorP12, password) = await _certService.GenerateProfileAsync(
                    duids, auth.access_token, auth.userId, auth.inputEmailID, profileDir, caPath, level, progress);
            }
            else
            {
                // Reuse the author keypair; only the distributor cert is regenerated.
                distributorP12 = await _certService.RegenerateDistributorAsync(
                    profileDir, duids, auth.access_token, auth.userId, auth.inputEmailID, caPath, level, progress);
                authorP12 = Path.Combine(profileDir, AuthorFileName);
                password = File.ReadAllText(Path.Combine(profileDir, PasswordFileName)).Trim();
            }

            return new CertificateProfile(authorP12, distributorP12, password, profileDir, profileName, level);
        }

        /// <summary>
        /// True when a usable profile for <paramref name="level"/> already exists in the store — i.e.
        /// the user "already has" that certificate, so nothing has to be minted for it. Used by the
        /// heads to decide whether requiring Partner means creating a new certificate.
        /// </summary>
        public static bool HasProfile(string storePath, CertificatePrivilegeLevel level) =>
            HasUsableAuthorCert(Path.Combine(storePath, ProfileName(level)));

        /// <summary>True if the profile dir has a loadable, unexpired author cert.</summary>
        public static bool HasUsableAuthorCert(string certDir)
        {
            try
            {
                var authorP12 = Path.Combine(certDir, AuthorFileName);
                var passwordFile = Path.Combine(certDir, PasswordFileName);
                if (!File.Exists(authorP12) || !File.Exists(passwordFile))
                    return false;

                var password = File.ReadAllText(passwordFile).Trim();
#pragma warning disable SYSLIB0057 // matches the proven loader used elsewhere; legacy PKCS#12 support
                using var cert = new X509Certificate2(authorP12, password, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
                return cert.NotAfter.Date >= DateTime.Today;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Cert] Author cert check failed for '{certDir}': {ex.Message}");
                return false;
            }
        }

        /// <summary>DUIDs already covered by the distributor cert in <paramref name="certDir"/>.</summary>
        public static HashSet<string> GetCoveredDuids(string certDir)
        {
            try
            {
                var passwordFile = Path.Combine(certDir, PasswordFileName);
                if (!File.Exists(passwordFile))
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var password = File.ReadAllText(passwordFile).Trim();
                var distributor = Path.Combine(certDir, DistributorFileName);
                return new CertificateHelper().GetCertificateDuids(distributor, password);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Cert] Could not read covered DUIDs from '{certDir}': {ex.Message}");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>The DUID set a (re)generated distributor cert should cover: this TV first, then
        /// manual entries, then already-covered ones — capped at Samsung's per-cert limit.</summary>
        public static IReadOnlyCollection<string> BuildDistributorDuids(
            string currentDuid, IEnumerable<string> manual, IEnumerable<string> covered)
        {
            var ordered = new List<string>();
            void Add(string d)
            {
                if (TizenDuid.IsValid(d) && !ordered.Contains(d.Trim(), StringComparer.OrdinalIgnoreCase))
                    ordered.Add(d.Trim());
            }

            Add(currentDuid);
            foreach (var d in manual) Add(d);
            foreach (var d in covered) Add(d);

            return ordered.Count > MaxDistributorDuids
                ? ordered.Take(MaxDistributorDuids).ToList()
                : ordered;
        }
    }
}
