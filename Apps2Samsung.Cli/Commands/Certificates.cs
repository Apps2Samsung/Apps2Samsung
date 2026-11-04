using System.Security.Cryptography.X509Certificates;
using Apps2Samsung.Certificate;
using Apps2Samsung.Helpers.Core;
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
            manualDuids: s.Config.ManualDuids.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            forceLogin: s.Options.ForceLogin,
            onLoginStarting: null,
            progress: key => s.Out.Progress(s.Strings.GetString(key)),
            beforeLoginAsync: null,
            ct: ct);

        return (new SigningProfile(profile.AuthorP12, profile.DistributorP12, profile.Password, profile.ProfileDir), duid, profile);
    }

    /// <summary>
    /// The certificate choice for this run: Partner with --partner, otherwise the desktop app's
    /// Automatic / Public / Partner setting, so both sign at the same level.
    /// </summary>
    public static string Preference(CliServices s) =>
        s.Options.Partner ? CertificatePreference.Partner : s.Desktop.CertificatePreference;

    /// <summary>The level a certificate made outside an install is made at, as the desktop's PrepareCertificateAsync.</summary>
    public static CertificatePrivilegeLevel Level(CliServices s) =>
        CertificatePreference.ResolveLevel(Preference(s), requirePartner: false);

    /// <summary>
    /// --tizen-cert's SDK distributor (#988): the level named after the flag, else Partner with
    /// --partner or when the package needs it, else Public. Platform only when asked for by name.
    /// </summary>
    public static TizenDistributorLevel TizenLevel(CliServices s, bool requirePartner = false) => s.Options.TizenLevel switch
    {
        "partner" => TizenDistributorLevel.Partner,
        "platform" => TizenDistributorLevel.Platform,
        "public" => TizenDistributorLevel.Public,
        _ => s.Options.Partner || requirePartner ? TizenDistributorLevel.Partner : TizenDistributorLevel.Public,
    };

    /// <summary>--tizen-chain's signer generation, or null for the level's default (legacy where the SDK has one).</summary>
    public static TizenDistributorChain? TizenChain(CliServices s) => s.Options.TizenChain switch
    {
        "legacy" => TizenDistributorChain.Legacy,
        "new" => TizenDistributorChain.New,
        _ => null,
    };

    /// <summary>
    /// The Tizen signing profile for <paramref name="level"/> (#988): the one on this machine while
    /// its author certificate is valid, otherwise made now from the SDK files in Core. No TV, no
    /// DUID and no sign-in are involved; --force-login makes a new one regardless.
    /// </summary>
    public static async Task<(SigningProfile Profile, TizenCertificateProfile Raw)> EnsureTizenAsync(CliServices s, TizenDistributorLevel level)
    {
        var chain = TizenChain(s);
        if (chain is { } asked && !TizenSdkAssets.HasDistributorSigner(level, asked))
            throw new InvalidOperationException($"The Tizen SDK has no {asked.ToString().ToLowerInvariant()} distributor signer for {level}; Platform only exists as new.");
        var name = TizenProfileStore.ProfileName(level, chain);
        var existed = TizenProfileStore.HasProfile(CliPaths.CertificateStore, level, chain);
        s.Out.Progress(existed && !s.Options.ForceLogin
            ? $"Reusing the {name} certificate on this machine."
            : $"Making a {name} certificate (no Samsung account, no DUID).");
        var raw = await TizenProfileStore.EnsureAsync(
            CliPaths.CertificateStore, level, chain, force: s.Options.ForceLogin,
            progress: key => s.Out.Progress(s.Strings.GetString(key)));
        return (new SigningProfile(raw.AuthorP12, raw.DistributorP12, raw.Password, raw.ProfileDir), raw);
    }

    /// <summary>
    /// The TV's DUID for an install that signs with a Tizen certificate (#988): the certificate does
    /// not need it, the package-id renames remembered per TV do (#839). A TV that will not hand it
    /// out just gets no rename memory, instead of no install.
    /// </summary>
    public static async Task<string> TryReadDuidAsync(CliServices s, string tvIp)
    {
        try
        {
            s.Out.Progress("Reading the TV's DUID…");
            return await TizenDuidReader.ReadAsync(s.Sdb, tvIp, progress: s.Out.Progress);
        }
        catch (Exception ex)
        {
            s.Out.Warn($"could not read the TV's DUID ({ex.Message}); the Tizen certificate does not need it.");
            return string.Empty;
        }
    }

    public sealed record ProfileState(string Level, bool Exists, bool Usable, string? ValidFrom, string? ValidUntil, IReadOnlyList<string> Duids, string? Path);

    /// <summary>What is on disk for a Tizen level and signer generation (#988); no DUIDs, as a Tizen distributor names no TV.</summary>
    public static ProfileState DescribeTizen(TizenDistributorLevel level, TizenDistributorChain? chain = null) =>
        Describe(TizenProfileStore.ProfileDir(CliPaths.CertificateStore, level, chain), TizenProfileStore.ProfileName(level, chain)) with { Duids = Array.Empty<string>() };

    /// <summary>Every Tizen profile folder the SDK files can make (#988), in a stable order.</summary>
    public static IEnumerable<(TizenDistributorLevel Level, TizenDistributorChain Chain)> TizenProfiles()
    {
        foreach (var level in Enum.GetValues<TizenDistributorLevel>())
            foreach (var chain in new[] { TizenDistributorChain.Legacy, TizenDistributorChain.New })
                if (TizenSdkAssets.HasDistributorSigner(level, chain))
                    yield return (level, chain);
    }

    /// <summary>What is on disk for a level, without touching any TV.</summary>
    public static ProfileState Describe(CertificatePrivilegeLevel level) =>
        Describe(Path.Combine(CliPaths.CertificateStore, CertificateProvisioningService.ProfileName(level)), level.ToString());

    private static ProfileState Describe(string dir, string levelName)
    {
        var author = Path.Combine(dir, "author.p12");
        var passwordFile = Path.Combine(dir, "password.txt");
        if (!File.Exists(author) || !File.Exists(passwordFile))
            return new ProfileState(levelName, false, false, null, null, Array.Empty<string>(), null);

        string? from = null, until = null;
        try
        {
            var password = File.ReadAllText(passwordFile).Trim();
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(author, password, PlatformService.SigningCertificateFlags);
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
        return new ProfileState(levelName, true, usable, from, until, duids, dir);
    }
}
