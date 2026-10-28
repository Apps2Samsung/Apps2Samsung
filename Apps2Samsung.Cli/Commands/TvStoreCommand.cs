using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
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
        var ip = s.Options.Tv ?? s.Options.Arguments.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("tvstore needs --tv <ip>", Output.Usage);
        var level = Certificates.Level(s);

        s.Out.Progress($"Looking for TV-Store on {ip}…");
        TvStoreInstallState state;
        try
        {
            state = await TvStorePackage.GetInstallStateAsync(s.Sdb, ip);
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
        var pem = SigningCertificatePem.FromProfile(profile);

        var progress = new Progress<string>(key => s.Out.Progress(s.Strings.GetString(key)));
        await using var store = await TvStoreClient.AttachAsync(s.Sdb, ip, progress, ct);
        s.Out.Progress(s.Strings.GetString("lblToolboxTvStoreArming"));
        await store.ArmAsync(pem, ct);
        var status = await store.StatusAsync(ct);

        return s.Out.Result(
            new { ok = true, tv = ip, duid, profile = profileName, tvStoreVersion = store.Version, armed = status.Armed, ownIp = status.OwnIp },
            new[]
            {
                $"TV-Store v{store.Version} on {ip} is armed with {profileName} (DUID {duid}).",
                $"It signs and installs on its own from now on. On the TV, now set Developer Mode's Host PC IP to the TV's own network address ({status.OwnIp ?? ip}, not 127.0.0.1) and restart the TV.",
            });
    }
}
