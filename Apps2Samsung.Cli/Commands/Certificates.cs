using System.Security.Cryptography.X509Certificates;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Install;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

/// <summary>The certificate steps the install and <c>cert --prepare</c> share.</summary>
public static class Certificates
{
    /// <summary>
    /// A signing profile for <paramref name="tvIp"/> at <paramref name="level"/>: reused when one on
    /// this machine already covers the TV, otherwise made now, which signs in to Samsung in a browser.
    /// </summary>
    public static async Task<(SigningProfile Profile, string Duid, CertificateProfile Raw)> EnsureAsync(
        CliServices s, string tvIp, CertificatePrivilegeLevel level, CancellationToken ct)
    {
        s.Out.Progress("Reading the TV's DUID…");
        var duid = await TizenDuidReader.ReadAsync(s.Sdb, tvIp, progress: s.Out.Progress);
        return await EnsureForDuidAsync(s, duid, level, ct);
    }

    /// <summary>
    /// The same, for a TV whose DUID is already known (<c>cert --prepare --duids</c> without <c>--tv</c>):
    /// nothing is read over SDB, so it works while the TV's Host PC IP points elsewhere.
    /// </summary>
    public static async Task<(SigningProfile Profile, string Duid, CertificateProfile Raw)> EnsureForDuidAsync(
        CliServices s, string duid, CertificatePrivilegeLevel level, CancellationToken ct)
    {
        var caPath = CliPaths.EnsureCaFiles();

        var covered = CertificateProvisioningService.HasProfile(CliPaths.CertificateStore, level)
            && CertificateProvisioningService.GetCoveredDuids(Path.Combine(CliPaths.CertificateStore, CertificateProvisioningService.ProfileName(level))).Contains(duid);
        s.Out.Progress(covered && !s.Options.ForceLogin
            ? $"Reusing the {level} certificate that already covers this TV."
            : $"A {level} certificate for this TV has to be created.");

        var profile = await s.Provisioning.EnsureAsync(
            deviceDuid: duid,
            level: level,
            storePath: CliPaths.CertificateStore,
            caPath: caPath,
            manualDuids: s.Options.ManualDuids.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            forceLogin: s.Options.ForceLogin,
            onLoginStarting: null,
            progress: key => s.Out.Progress(s.Strings.GetString(key)),
            beforeLoginAsync: null,
            ct: ct);

        return (new SigningProfile(profile.AuthorP12, profile.DistributorP12, profile.Password, profile.ProfileDir), duid, profile);
    }

    public sealed record ProfileState(string Level, bool Exists, bool Usable, string? ValidFrom, string? ValidUntil, IReadOnlyList<string> Duids, string? Path);

    /// <summary>What is on disk for a level, without touching any TV.</summary>
    public static ProfileState Describe(CertificatePrivilegeLevel level)
    {
        var dir = Path.Combine(CliPaths.CertificateStore, CertificateProvisioningService.ProfileName(level));
        var author = Path.Combine(dir, "author.p12");
        var passwordFile = Path.Combine(dir, "password.txt");
        if (!File.Exists(author) || !File.Exists(passwordFile))
            return new ProfileState(level.ToString(), false, false, null, null, Array.Empty<string>(), null);

        string? from = null, until = null;
        try
        {
            var password = File.ReadAllText(passwordFile).Trim();
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(author, password, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
            from = cert.NotBefore.ToString("yyyy-MM-dd");
            until = cert.NotAfter.ToString("yyyy-MM-dd");
        }
        catch
        {
            // Reported as present but not usable below.
        }
        var usable = CertificateProvisioningService.HasUsableAuthorCert(dir);
        var duids = CertificateProvisioningService.GetCoveredDuids(dir).OrderBy(d => d, StringComparer.Ordinal).ToList();
        return new ProfileState(level.ToString(), true, usable, from, until, duids, dir);
    }
}
