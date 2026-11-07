using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// <c>tvstore --tv &lt;ip&gt;</c>: arms the TV-Store app on a TV (#844) by handing it the signing
/// certificate, after which the TV installs catalog apps on its own. The certificate is the one an
/// install would use; it is made here (Samsung sign-in in the browser) when none covers the TV yet.
/// </summary>
public static class TvStoreCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        if (s.Options.Arguments.FirstOrDefault() is "log" or "logs")
            return await LogAsync(s, ct);

        var ip = s.Options.Tv ?? s.Options.Arguments.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("tvstore needs --tv <ip>", Output.Usage);
        var level = Certificates.Level(s);

        s.Out.Progress($"Looking for TV-Store on {ip}…");
        TvStoreInstallState state;
        try
        {
            // This machine's package-id renames (#839): a TV-Store installed under a new id is TV-Store too.
            state = await TvStorePackage.GetInstallStateAsync(s.Sdb, ip, s.Config.PackageIdRenamesJson);
        }
        catch (TvStoreUnreachableException ex)
        {
            // No listing is not "not installed" (#889): the TV's sdbd refused this machine.
            return s.Out.Fail(
                $"{ip} did not list its apps ({ex.Reason}), so TV-Store can't be checked. In Developer Mode on the TV, " +
                "Developer Mode must be on and Host PC IP must be this machine's current address while arming; restart the TV after changing either.");
        }
        if (state == TvStoreInstallState.Outdated)
        {
            return s.Out.Fail(
                $"An old TV-Store ({TvStorePackage.LegacyAppId}) is installed on {ip}. Update it first, like any package: " +
                $"apps2samsung-cli install TV-Store.wgt --tv {ip} (the .wgt comes from {TvStorePackage.RepositoryUrl}).");
        }
        if (state != TvStoreInstallState.Installed)
        {
            return s.Out.Fail(
                $"TV-Store ({TvStorePackage.PackageId}) is not installed on {ip}. Install it first, like any package: " +
                $"apps2samsung-cli install TV-Store.wgt --tv {ip} (the .wgt comes from {TvStorePackage.RepositoryUrl}).");
        }

        var (ensured, duid, _) = await Certificates.EnsureAsync(s, ip, level, ct);
        // As the desktop's toolbox: whichever profile on this machine covers the TV, Public first.
        var profile = SigningCertificatePem.FindProfileCovering(CliPaths.CertificateStore, duid) ?? ensured;
        var profileName = Path.GetFileName(profile.ProfileDir);
        s.Out.Progress("Opening the certificate…");
        // Every profile in the store goes along, the covering one first (#44).
        var pems = SigningCertificatePem.OpenAll(profile, SigningCertificatePem.ProfilesIn(CliPaths.CertificateStore));

        var progress = new Progress<string>(key => s.Out.Progress(s.Strings.GetString(key)));
        await using var store = await TvStoreClient.AttachAsync(s.Sdb, ip, progress, s.Config.PackageIdRenamesJson, ct);
        s.Out.Progress(s.Strings.GetString("lblToolboxTvStoreArming"));
        await store.ArmAsync(pems, ct);
        var status = await store.StatusAsync(ct);

        // What is left to do depends on the Host PC IP the TV has right now, as in both apps (#960).
        var tv = await TizenDeveloperInfo.ReadAsync(new NetworkDevice { IpAddress = ip }, ct);
        var nextStep = string.Format(
            s.Strings.GetString(TvStoreNextSteps.MessageKey(TvStoreNextSteps.For(tv.DeveloperIP, status.OwnIp))),
            store.Version, status.OwnIp ?? ip);

        return s.Out.Result(
            new { ok = true, tv = ip, duid, profile = profileName, tvStoreVersion = store.Version, tvStoreAppId = store.AppId, armed = status.Armed, ownIp = status.OwnIp },
            new[]
            {
                $"TV-Store v{store.Version} on {ip} is armed with {profileName} (DUID {duid}).",
                nextStep,
            });
    }

    /// <summary>
    /// <c>tvstore log --tv &lt;ip&gt; [-o &lt;file&gt;]</c>: TV-Store's own log from the TV
    /// (GET :8077/log.txt), saved in the current folder or to the given file. TV-Store has to be
    /// open on the TV; no Developer Mode needed.
    /// </summary>
    private static async Task<int> LogAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Tv ?? s.Options.Arguments.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("tvstore log needs --tv <ip>", Output.Usage);

        s.Out.Progress($"Getting the TV-Store log from {ip}…");
        string path;
        try
        {
            path = string.IsNullOrWhiteSpace(s.Options.OutputPath)
                ? await TvStoreLog.SaveAsync(ip, Directory.GetCurrentDirectory(), ct)
                : await TvStoreLog.SaveToFileAsync(ip, s.Options.OutputPath, ct);
        }
        catch (TvStoreLogUnavailableException ex)
        {
            return s.Out.Fail(ex.Message);
        }

        return s.Out.Result(
            new { ok = true, tv = ip, file = path, url = TvStoreLog.UrlFor(ip) },
            new[] { $"TV-Store log from {ip} saved to {path}." });
    }
}
