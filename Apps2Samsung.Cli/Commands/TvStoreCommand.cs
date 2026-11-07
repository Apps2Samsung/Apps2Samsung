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
        if (s.Options.Arguments.FirstOrDefault() is "sources" or "source")
            return await SourcesAsync(s, ct);

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
    /// <c>tvstore sources --tv &lt;ip&gt; [name|id ...]</c> (#1018, TV-Store#82): hands the signed
    /// repositories of `sources` (every one, or those named) to TV-Store with the keys approved here.
    /// The TV asks the person at it for each; this waits for those answers. Unsigned sources stay here.
    /// </summary>
    private static async Task<int> SourcesAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("tvstore sources needs --tv <ip>", Output.Usage);
        var wanted = s.Options.Arguments.Skip(1).ToList();
        var repositories = Apps2Samsung.Catalog.UserSources.Parse(s.Desktop.UserSourcesJson)
            .Where(Apps2Samsung.Catalog.UserSources.IsApprovedRepository)
            .Where(src => wanted.Count == 0 || wanted.Any(w =>
                string.Equals(w, src.RepositoryId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w, Apps2Samsung.Catalog.UserSources.DisplayName(src), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (repositories.Count == 0)
            return s.Out.Fail(wanted.Count == 0
                ? "No signed repository to send: add one with `sources add <https://…/repository.json.signed>`. Unsigned sources never go to the TV."
                : $"No signed repository matches {string.Join(", ", wanted)}; `sources` lists them.", Output.Usage);

        var progress = new Progress<string>(key => s.Out.Progress(s.Strings.GetString(key)));
        try
        {
            await using var store = await TvStoreClient.AttachAsync(s.Sdb, ip, progress, s.Config.PackageIdRenamesJson, ct);
            s.Out.Progress($"Sending {repositories.Count} source(s) to TV-Store on {ip}: confirm each one on the TV…");
            var results = await store.AddSourcesAsync(repositories, ct);
            var lines = results.Select(r => r.Added
                ? $"{r.Name ?? r.RepositoryId}: added on the TV."
                : $"{r.Name ?? r.RepositoryId}: not added ({r.Error})").ToList();
            var added = results.Count(r => r.Added);
            return s.Out.Result(new { ok = added > 0, tv = ip, results }, lines, added == results.Count ? Output.Ok : Output.Stopped);
        }
        catch (TvStoreNotInstalledException ex)
        {
            return s.Out.Fail(ex.IsOutdated
                ? $"An old TV-Store is installed on {ip}; update it first (the .wgt comes from {TvStorePackage.RepositoryUrl})."
                : $"TV-Store is not installed on {ip}; install it first (the .wgt comes from {TvStorePackage.RepositoryUrl}).");
        }
        catch (TvStoreUnreachableException ex)
        {
            return s.Out.Fail($"TV-Store on {ip} could not be reached ({ex.Reason}). Developer Mode must be on with this machine as Host PC IP, and TV-Store open on the TV.");
        }
        catch (NotSupportedException ex)
        {
            return s.Out.Fail(ex.Message);
        }
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
