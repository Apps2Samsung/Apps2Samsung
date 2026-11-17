using System;
using System.IO;
using System.Threading.Tasks;
using Apps2Samsung.Extensions;

namespace Apps2Samsung.Certificate
{
    /// <summary>A Tizen certificate profile on disk (#988): the three files a Samsung profile has, and the SDK level.</summary>
    public sealed record TizenCertificateProfile(
        string AuthorP12,
        string DistributorP12,
        string Password,
        string ProfileDir,
        string ProfileName,
        TizenDistributorLevel Level,
        TizenDistributorChain Chain);

    /// <summary>
    /// The Tizen certificate profiles in the certificate store (#988), next to the Samsung ones
    /// (<see cref="CertificateProvisioningService"/>) and never mixed with them: their own
    /// <c>"Tizen - Public/Partner/Platform"</c> folders, no sign-in, no DUID. A profile is reused
    /// while its author certificate is valid; otherwise a new one is made on the spot.
    /// </summary>
    public static class TizenProfileStore
    {
        public const string ProfileBaseName = "Tizen";

        /// <summary>
        /// The profile folder name, e.g. "Tizen - Public" for the level's default signer generation
        /// and "Tizen - Public New" for the other one (<see cref="TizenSdkAssets.DefaultChain"/>).
        /// </summary>
        public static string ProfileName(TizenDistributorLevel level, TizenDistributorChain? chain = null)
        {
            var resolved = chain ?? TizenSdkAssets.DefaultChain(level);
            return resolved == TizenSdkAssets.DefaultChain(level)
                ? $"{ProfileBaseName} - {level}"
                : $"{ProfileBaseName} - {level} {resolved}";
        }

        /// <summary>True for a folder name one of ours, as <see cref="ProfileName"/> makes it.</summary>
        public static bool IsTizenProfileName(string? name) =>
            name is not null && name.StartsWith(ProfileBaseName + " - ", StringComparison.Ordinal);

        /// <summary>The level and signer generation a profile folder name is for, or null when it is not one of ours.</summary>
        public static (TizenDistributorLevel Level, TizenDistributorChain Chain)? Parse(string? profileName)
        {
            if (!IsTizenProfileName(profileName))
                return null;
            var parts = profileName![(ProfileBaseName.Length + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is 0 or > 2 || !Enum.TryParse<TizenDistributorLevel>(parts[0], out var level))
                return null;
            if (parts.Length == 1)
                return (level, TizenSdkAssets.DefaultChain(level));
            return Enum.TryParse<TizenDistributorChain>(parts[1], out var chain) && chain != TizenSdkAssets.DefaultChain(level) && TizenSdkAssets.HasDistributorSigner(level, chain)
                ? (level, chain)
                : null;
        }

        /// <summary>The level a profile folder name is for, or null when it is not one of ours.</summary>
        public static TizenDistributorLevel? LevelOf(string? profileName) => Parse(profileName)?.Level;

        public static string ProfileDir(string storePath, TizenDistributorLevel level, TizenDistributorChain? chain = null) =>
            Path.Combine(storePath, ProfileName(level, chain));

        /// <summary>True when the store holds a usable (loadable, unexpired author) profile for the level and generation.</summary>
        public static bool HasProfile(string storePath, TizenDistributorLevel level, TizenDistributorChain? chain = null)
        {
            var dir = ProfileDir(storePath, level, chain);
            return CertificateProvisioningService.HasUsableAuthorCert(dir)
                && File.Exists(Path.Combine(dir, TizenLocalCertificateService.DistributorFileName));
        }

        /// <summary>
        /// The profile for <paramref name="level"/> and <paramref name="chain"/> (the level's default
        /// generation when null): the one in the store when usable, otherwise a new one (also with
        /// <paramref name="force"/>). Never waits on anything but the disk.
        /// </summary>
        public static async Task<TizenCertificateProfile> EnsureAsync(
            string storePath, TizenDistributorLevel level, TizenDistributorChain? chain = null, bool force = false, string? commonName = null, ProgressCallback? progress = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
            var resolved = chain ?? TizenSdkAssets.DefaultChain(level);
            var dir = ProfileDir(storePath, level, resolved);
            if (!force && HasProfile(storePath, level, resolved))
            {
                var password = (await File.ReadAllTextAsync(Path.Combine(dir, TizenLocalCertificateService.PasswordFileName))).Trim();
                return new TizenCertificateProfile(
                    Path.Combine(dir, TizenLocalCertificateService.AuthorFileName),
                    Path.Combine(dir, TizenLocalCertificateService.DistributorFileName),
                    password, dir, ProfileName(level, resolved), level, resolved);
            }
            var created = await TizenLocalCertificateService.CreateProfileAsync(dir, level, resolved, commonName, progress);
            CertificateBackups.Changed(); // #1068
            return created;
        }
    }
}
