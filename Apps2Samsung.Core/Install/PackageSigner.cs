using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Certificate;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Packaging;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Install
{
    /// <summary>A signed copy: where it went and the ids its manifest carries.</summary>
    public sealed record SignedPackage(string Path, string? AppId, string? PackageId);

    /// <summary>The signer refused the package; the message is the engine's own.</summary>
    public sealed class PackageSignException : Exception
    {
        public PackageSignException(string message) : base(message) { }
    }

    /// <summary>
    /// Signing without installing (#979): a local .wgt/.tpk, signed with the certificate that covers
    /// a TV, written as a new file, for tools that install another way (a root service on the TV,
    /// TV-Store's upload page). Shared by the CLI's <c>sign</c>, the desktop and the phone, so the
    /// three write the same file for the same input. The input is never changed: the signer re-signs
    /// in place, so it works on a staged copy that is deleted afterwards.
    /// </summary>
    public static class PackageSigner
    {
        /// <summary>True for the package files the signer takes.</summary>
        public static bool IsPackage(string path)
        {
            var ext = System.IO.Path.GetExtension(path);
            return ext.Equals(".wgt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tpk", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The name the signed copy is offered under: <c>app.wgt</c> → <c>app-signed.wgt</c>.</summary>
        public static string SuggestedOutputName(string inputPath) =>
            System.IO.Path.GetFileNameWithoutExtension(inputPath) + "-signed" + System.IO.Path.GetExtension(inputPath);

        /// <summary>
        /// Signs <paramref name="inputPath"/> with <paramref name="profile"/> and writes the result to
        /// <paramref name="outputPath"/> (its folder is made when missing). <paramref name="progress"/>
        /// gets en.json keys, as the installer reports them.
        /// </summary>
        /// <exception cref="ArgumentException">Not a .wgt/.tpk, or the output is the input.</exception>
        /// <exception cref="FileNotFoundException">No such input.</exception>
        /// <exception cref="CertificateNotYetValidException">The certificate's validity has not started: Tizen would refuse the signature.</exception>
        /// <exception cref="PackageSignException">The signer failed; the message says what it printed.</exception>
        public static async Task<SignedPackage> SignCopyAsync(
            ISdbEngine sdb, string inputPath, string outputPath, SigningProfile profile,
            Action<string>? progress = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(sdb);
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

            if (!File.Exists(inputPath))
                throw new FileNotFoundException("No such package.", inputPath);
            if (!IsPackage(inputPath))
                throw new ArgumentException("Not a .wgt or .tpk.", nameof(inputPath));
            if (SamePath(inputPath, outputPath))
                throw new ArgumentException("The output must be another file: the input is never changed.", nameof(outputPath));

            // Tizen checks the signature against the TV's clock: a certificate whose start date is
            // still in the future signs fine here and is refused there, so stop before signing.
            var validity = CertificateValidity.CheckSigningProfile(profile.AuthorP12, profile.DistributorP12, profile.Password);
            if (validity.IsNotYetValid)
                throw new CertificateNotYetValidException(validity);
            ct.ThrowIfCancellationRequested();

            progress?.Invoke("statusSigningPackage");
            using var staged = StagedPackage.Create(inputPath);
            var result = await sdb.ResignAsync(staged.FilePath, profile.AuthorP12, profile.DistributorP12, profile.Password).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                var why = string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() : result.Error.Trim();
                throw new PackageSignException(string.IsNullOrWhiteSpace(why) ? $"exit code {result.ExitCode}" : why);
            }
            ct.ThrowIfCancellationRequested();

            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(staged.FilePath, outputPath, overwrite: true);

            var (appId, packageId) = await WgtManifest.ReadIdsAsync(outputPath).ConfigureAwait(false);
            return new SignedPackage(System.IO.Path.GetFullPath(outputPath), appId, packageId);
        }

        private static bool SamePath(string a, string b)
        {
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), comparison);
        }
    }
}
