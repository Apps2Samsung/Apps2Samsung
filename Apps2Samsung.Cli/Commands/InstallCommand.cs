using Apps2Samsung.Localization;
using System.IO.Compression;
using System.Net.Http.Headers;
using Apps2Samsung.Catalog;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Install;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Remote;
using Apps2Samsung.Sdb;
using Apps2Samsung.Services;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Cli.Commands;

public static class InstallCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var target = s.Options.Arguments.FirstOrDefault();
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli install <app|file> --tv <ip>   (`install --help` for the options)", Output.Usage);

        // An install link from a website or another tool (#1066): it names a catalog app, never a file to fetch.
        InstallLink? link = null;
        if (InstallLink.LooksLikeLink(target))
        {
            if (!InstallLink.TryParse(target, out link))
                return s.Out.Fail("not an install link Apps2Samsung reads: apps2samsung://install?app=<catalog name, package id or app id>[&file=<file or variant>]", Output.Usage);
            if (link!.Kind == InstallLinkKind.Source)
                return s.Out.Fail($"that link adds a source, it does not install an app: `apps2samsung-cli sources add {link.SourceUrl}` adds it", Output.Usage);
        }

        // ---- What to install: a local file, or a catalog app and one of its files ----
        GitHubRelease? release = null;
        Asset? asset = null;
        string? localFile = null;
        if (link is null && File.Exists(target) && (target.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
        {
            localFile = Path.GetFullPath(target);
        }
        else
        {
            s.Out.Progress("Loading the app catalog…");
            var wantVersion = !string.IsNullOrWhiteSpace(s.Options.Version);
            var catalog = await s.Catalog.LoadAsync(s.Options.AllVersions || wantVersion, ct);
            // Adult-only entries are found by name only with --adult, as `catalog` lists them (#850).
            var visible = CommunityAppList.Visible(catalog.Entries, s.Options.Adult).ToList();
            // Several sources can offer one app (#1018): --from picks one; they are never merged.
            if (!string.IsNullOrWhiteSpace(s.Options.From))
                visible = visible.Where(e => SourcesCommand.Matches(e.Source, s.Options.From)).ToList();
            if (link is not null)
            {
                // Exact only: a name, a package id or an app id, never a part of a name.
                var named = InstallLink.Resolve(link, visible);
                if (named is null)
                    return InstallLink.Resolve(link, catalog.Entries) is { } hidden && !s.Options.Adult && hidden.Entries.All(e => e.IsAdult)
                        ? s.Out.Fail($"The link names {hidden.Name}, an 18+ app: rerun with --adult to install it.", Output.Usage, new { link = link.ToSchemeUrl() })
                        : s.Out.Fail($"The link names '{link.App}', which is not in the catalog{(string.IsNullOrWhiteSpace(s.Options.From) ? "" : $" from {s.Options.From}")}. `apps2samsung-cli catalog` lists the apps.",
                            details: new { link = link.ToSchemeUrl() });
                target = named.Name;
                if (named.FileMissing)
                    s.Out.Warn($"{named.Name} no longer has the file '{link.File}' the link names; installing its default file.");
                else if (named.File is not null && string.IsNullOrWhiteSpace(s.Options.File))
                    s.Options.File = named.File;
            }
            var (match, candidates) = CatalogLoader.Find(visible, target);
            if (match is null)
            {
                // An app the catalog stopped distributing (#1033): say why instead of "not found". No link to it.
                if (candidates.Count == 0 && (RemovedApps.Named(target, catalog.RemovedApps)
                        ?? (RemovedApps.Search(target, catalog.RemovedApps) is { Count: 1 } one ? one[0] : null)) is { } gone)
                {
                    var why = InstalledCommand.Removal(s, gone);
                    return s.Out.Fail($"{why.Name} was removed from the catalogue{(why.Since is null ? "" : $" on {why.Since}")} and cannot be installed. {why.Text}{(why.Note is null ? "" : $" {why.Note}")}",
                        Output.Failed, new { removed = why });
                }
                return candidates.Count == 0
                    ? s.Out.Fail($"No catalog app matches '{target}'. `apps2samsung-cli catalog {target}` searches descriptions too.")
                    : s.Out.Fail($"'{target}' matches several apps; use the exact name: {string.Join(" | ", candidates.Select(c => c.Name))}",
                        details: new { candidates = candidates.Select(c => c.Name) });
            }
            var offeredBy = candidates.Select(c => c.Source).DistinctBy(src => src.Key).ToList();
            if (offeredBy.Count > 1)
                return s.Out.Fail($"{match.Name} is offered by {offeredBy.Count} sources: {string.Join(", ", offeredBy.Select(SourcesCommand.Label))}. Pick one with --from <{string.Join("|", offeredBy.Select(SourcesCommand.FromValue))}>.",
                    Output.Usage, new { sources = offeredBy.Select(src => new { label = SourcesCommand.Label(src), from = SourcesCommand.FromValue(src) }) });
            release = match;
            if (wantVersion)
            {
                var versions = visible.Where(e => string.Equals(e.Name, match.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                release = versions.FirstOrDefault(v => string.Equals(v.TagName, s.Options.Version, StringComparison.OrdinalIgnoreCase));
                if (release is null)
                    return s.Out.Fail($"{match.Name} has no release '{s.Options.Version}'. Known: {string.Join(", ", versions.Select(v => v.TagName))}");
            }

            if (!string.IsNullOrWhiteSpace(s.Options.File))
            {
                asset = release.Assets.FirstOrDefault(a =>
                    string.Equals(a.FileName, s.Options.File, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(a.FileName), s.Options.File, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Variant, s.Options.File, StringComparison.OrdinalIgnoreCase));
                if (asset is null)
                    return s.Out.Fail($"{release.Name} has no file or variant '{s.Options.File}'. Choose from: {string.Join(", ", release.Assets.Select(a => a.VariantLabel))}");
            }
            else
            {
                asset = release.Assets.FirstOrDefault(a => a.IsDefault) ?? release.Assets.FirstOrDefault();
                if (asset is null)
                    return s.Out.Fail($"{release.Name} has no installable file in its release.");
                if (release.HasVariants && release.Assets.Count > 1)
                    s.Out.Warn($"{release.Name} has {release.Assets.Count} variants; installing '{asset.VariantLabel}'. --file picks another: {string.Join(", ", release.Assets.Select(a => a.VariantLabel))}");
            }

            // A repository that failed its last check is listed from its last verified copy, never installed from (#1018).
            if (!release.Source.CanInstall)
                return s.Out.Fail(release.Source.InstallBlockedReason!);

            // A package that gains root: only when asked for by name, as the apps ask first.
            if (release.IsRoot && !s.Options.AllowRoot)
                return s.Out.Fail(
                    $"{release.Name} gains root on the TV and runs a background service from boot. After the install, set Developer Mode's Host PC IP to 127.0.0.1 and restart the TV; " +
                    "from then on this machine reaches the TV only through TV-Store's relay (TV-Store 0.3.14 or newer, turned on in its Settings), which the CLI dials by itself. Rerun with --allow-root to install it.",
                    Output.Stopped);

            // Values the package needs before it installs (#791).
            if (asset.UserSettings is { Fields.Count: > 0 } spec)
            {
                var problems = new List<string>();
                foreach (var field in spec.Fields)
                {
                    s.Options.Settings.TryGetValue(field.Key, out var value);
                    var key = UserSettingsStore.Validate(field, value);
                    if (key is not null)
                        problems.Add($"--set {field.Key}=<{field.Type}>  {field.Label}: {UserSettingsStore.Message(field, key, s.Strings.GetString)}");
                }
                if (problems.Count > 0)
                    return s.Out.Fail($"{release.Name} needs values before it installs{(spec.HelpUrl is null ? "" : $" (see {spec.HelpUrl})")}:\n  {string.Join("\n  ", problems)}",
                        details: new { settings = spec.Fields.Select(f => new { f.Key, f.Label, f.Type, f.Required, f.Pattern, f.Default, f.Min, f.Max, f.Disallow }), spec.HelpUrl });
                s.Config.AppUserSettingsJson = UserSettingsStore.Set(null, asset.FileName, spec, s.Options.Settings);
            }
        }
        var appName = release?.Name ?? Path.GetFileNameWithoutExtension(localFile!);

        // ---- The TV: reachable, and the pre-install checks both heads ask about ----
        s.Out.Progress($"Checking {ip}…");
        var device = await s.Network.ValidateManualTizenAddress(ip, ct);
        if (device is null)
            return s.Out.Fail($"No Samsung TV answers at {ip}. `apps2samsung-cli scan` lists the TVs on this network.");
        device = await TizenDeveloperInfo.ReadAsync(device, ct);
        s.Out.Heading($"[bold]{Views.Esc(appName)}[/]{(asset?.PackageVersion is { } ver ? $" v{Views.Esc(ver)}" : "")} [{Views.MutedHex}]{(s.Out.Unicode ? "→" : "->")}[/] [bold]{Views.Esc(ip)}[/] {Views.Esc(device.DeviceName)}");
        var localIp = LocalIp.Of(s);
        var guards = InstallGuards.Evaluate(device, new InstallGuardOptions
        {
            LocalIps = SafeLocalIps(s),
            ConfiguredLocalIp = localIp,
            SkipDevModeChecks = s.Options.SkipDevModeCheck,
        }, s.Network);
        // For testing (#1026): the Developer Mode off / restart checks are left out, and the run says so.
        if (s.Options.SkipDevModeCheck)
        {
            System.Diagnostics.Trace.WriteLine("[install] Developer Mode checks skipped (--skip-devmode-check)");
            s.Out.Warn("Developer Mode checks are off (--skip-devmode-check): the install goes straight to the SDB connect.");
        }
        var viaTvStore = s.Options.ViaTvStore;
        if (!viaTvStore)
        {
            foreach (var g in guards.Guards)
                s.Out.Warn($"{g.DefaultTitle}: {g.DefaultMessageWithDetail.Replace("\n\n", " ")}");
        }
        var tvIp = guards.CorrectedTvIp ?? ip;
        if (!viaTvStore && guards.Guards.Count > 0 && !s.Options.Yes)
        {
            // The checks are about SDB; TV-Store's upload page does not need any of it.
            if (await TvStoreReadyAsync(s, tvIp, ct))
            {
                s.Out.Warn("TV-Store is running on the TV: sending the package to it instead of installing over SDB.");
                viaTvStore = true;
            }
            else
            {
                return s.Out.Fail($"Stopped before installing: {guards.Guards.Count} check(s) above would most likely make it fail. Fix them, or rerun with --yes to try anyway.",
                    Output.Stopped, new { checks = guards.Guards.Select(g => new { kind = g.Kind.ToString(), g.DefaultTitle, message = g.DefaultMessageWithDetail }) });
            }
        }
        // Not with --skip-devmode-check (#1026): the tester wants the SDB connect's own error.
        else if (!viaTvStore && !s.Options.SkipDevModeCheck && !await SdbAnswersAsync(s, tvIp))
        {
            var tvStoreReady = await TvStoreReadyAsync(s, tvIp, ct);
            if (!tvStoreReady && !await SdbConnectsAsync(s, tvIp))
            {
                // Neither way in: stop here instead of downloading and failing on the DUID read (#975).
                // Only when the connect itself fails: an old sdbd without the capability service
                // still installs, as before.
                return s.Out.Fail($"{tvIp} does not answer over SDB, so nothing can be installed. Turn the TV off at the wall for 30 seconds and try again; " +
                    "if it still does not answer, check that Developer Mode's Host PC IP is this device's address.");
            }
            if (tvStoreReady)
            {
                // No SDB (Host PC IP 127.0.0.1 without TV-Store's relay, say), but TV-Store is there.
                s.Out.Warn("The TV does not answer over SDB, but TV-Store is running on it: sending the package to TV-Store instead.");
                viaTvStore = true;
            }
        }

        // ---- Download, or a working copy of the local file ----
        // The install renames, patches and re-signs the package in place, so it never runs on the
        // user's own file: the apps copy a picked file the same way.
        Directory.CreateDirectory(CliPaths.Downloads);
        string packagePath;
        if (localFile is not null)
        {
            // A folder of its own, so a file kept from an earlier --keep run is never copied onto itself.
            var staging = Path.Combine(CliPaths.Downloads, "local");
            Directory.CreateDirectory(staging);
            packagePath = Path.Combine(staging, Path.GetFileName(localFile));
            File.Copy(localFile, packagePath, overwrite: true);
        }
        else
        {
            packagePath = Path.Combine(CliPaths.Downloads, asset!.FileName);
            s.Out.Progress($"Downloading {asset.FileName} ({asset.DisplayText})…");
            await DownloadAsync(s, asset.DownloadUrl, packagePath, ct);
            // A signed repository's file installs only when its bytes are the ones it signed for (#1018).
            if (release!.Source.Kind == CatalogSourceKind.Repository)
            {
                s.Out.Progress("Checking the file against the repository's signed digest…");
                if (string.IsNullOrWhiteSpace(asset.Digest) || !Apps2Samsung.Catalog.Repositories.SignedRepositories.DigestMatches(packagePath, asset.Digest))
                {
                    try { File.Delete(packagePath); } catch { /* best-effort */ }
                    return s.Out.Fail($"{asset.FileName} is not the file {release.Source.Name} signed for (its SHA-256 does not match the repository's digest). Nothing was installed.");
                }
            }
        }

        try
        {
            if (viaTvStore)
                return await SendToTvStoreAsync(s, tvIp, packagePath, appName, asset?.PackageVersion, ct);

            // ---- Certificate ----
            // The desktop's Automatic / Public / Partner setting, unless --partner; a package that
            // needs Partner gets it either way.
            var needsPartner = (release?.RequiresPartner ?? false) || WgtPrivileges.RequiresPartner(packagePath);
            SigningProfile profile;
            string duid, certificate;
            if (Certificates.UseTizen(s))
            {
                // A Tizen certificate (#988): the SDK's, no Samsung account and no DUID in it. The TV
                // decides whether it takes the signature; the error below says so when it does not.
                var tizenLevel = Certificates.TizenLevel(s, needsPartner);
                (profile, var tizen) = await Certificates.EnsureTizenAsync(s, tizenLevel);
                certificate = tizen.ProfileName;
                duid = await Certificates.TryReadDuidAsync(s, tvIp);
            }
            else
            {
                var preference = Certificates.Preference(s);
                var level = CertificatePreference.ResolveLevel(preference, needsPartner);
                if (needsPartner && CertificatePreference.ResolveLevel(preference, false) != CertificatePrivilegeLevel.Partner)
                    s.Out.Progress("This package needs a Partner-level certificate; signing as Partner.");
                (profile, duid, _) = await Certificates.EnsureAsync(s, tvIp, level, ct);
                certificate = level.ToString();
            }

            // ---- Install ----
            var (appId, packageId) = await WgtManifest.ReadIdsAsync(packagePath);
            // Updating TV-Store through TV-Store's own relay (#1005): the TV drops this connection
            // while it restarts TV-Store; say so once up front, the install reads its end another way.
            if (await TvStoreSelfUpdate.AppliesAsync(tvIp, packagePath, s.Config.PackageIdRenamesJson, ct))
                s.Out.Warn(s.Strings.GetString(TvStoreSelfUpdate.NoticeKey));
            var lastPercent = -1;
            PackageIdRename? rename = null;
            var output = await s.Installer.InstallAsync(tvIp, packagePath, profile,
                progress: line =>
                {
                    // One line per percentage step would flood a log; every tenth is enough.
                    const string percentKey = "statusInstallingOnTvPercent|";
                    if (line.StartsWith(percentKey, StringComparison.Ordinal))
                    {
                        // On a terminal the bar takes every step; a log gets every tenth.
                        if (!int.TryParse(line[percentKey.Length..], out var pct) || (!s.Out.Rich && pct / 10 == lastPercent / 10 && pct != 100))
                            return;
                        lastPercent = pct;
                    }
                    // Core reports progress as en.json keys (ProgressText); the CLI's catalog is English.
                    s.Out.Progress(ProgressText.Localize(line, s.Strings.GetString));
                },
                stage: null,
                ct: ct,
                confirmRemove: () => Task.FromResult(s.Options.AllowRemove),
                packageId: new PackageIdPolicy(duid, s.Options.NewId, s.Options.OwnId, PackageIdCollisions.CatalogKey(release, asset)),
                confirmReplace: _ => Task.FromResult(s.Options.Replace),
                renamed: r => rename = r);

            // The ids as installed: a rename (#839) changed them after the read above.
            var (installedAppId, installedPackageId) = await WgtManifest.ReadIdsAsync(packagePath);
            var renamed = !string.Equals(installedPackageId, packageId, StringComparison.Ordinal);
            // Renamed because another app has this id (#1028): say why, the first thing after the result.
            var separate = rename?.CollidedWith is { } other
                ? string.Format(s.Strings.GetString("installedAsSeparateApp"), installedPackageId, other)
                : null;
            // Which file this TV got (#975), so `installed` and the menu's Update take the same variant
            // next time. In the desktop's settings.json, so the desktop app remembers it too.
            if (release is not null && asset is not null)
                RememberVariant(s, tvIp, duid, asset.AppId ?? appId, release, asset);
            // Launched here rather than by the installer, which swallows a failed launch: the result
            // has to say whether it really opened.
            var launched = false;
            if (s.Options.Launch && !string.IsNullOrWhiteSpace(installedAppId))
            {
                s.Out.Progress("Launching on TV…");
                var launch = await s.Sdb.LaunchAsync(tvIp, installedAppId);
                launched = launch.ExitCode == 0;
                if (!launched)
                    s.Out.Warn($"installed, but the TV did not open it: {(string.IsNullOrWhiteSpace(launch.Error) ? launch.Output.Trim() : launch.Error)}");
            }
            var resultLines = new List<string>
            {
                $"Installed {appName} on {tvIp}{(renamed ? $" under package id {installedPackageId} (remembered for this TV)" : "")}{(launched ? " and launched it" : "")}.",
            };
            if (separate is not null)
                resultLines.Add(separate);
            if (release?.IsRoot == true)
                resultLines.Add("Now set Developer Mode's Host PC IP to 127.0.0.1 and restart the TV. From then on this machine reaches the TV through TV-Store's relay: turn it on under TV-Store's Settings (TV-Store 0.3.14 or newer).");
            var lines = resultLines.ToArray();
            return s.Out.Result(
                new
                {
                    ok = true,
                    tv = tvIp,
                    app = appName,
                    file = Path.GetFileName(packagePath),
                    version = asset?.PackageVersion,
                    appId = installedAppId,
                    packageId = installedPackageId,
                    originalPackageId = renamed ? packageId : null,
                    separateFrom = rename?.CollidedWith,
                    certificate,
                    duid,
                    launched,
                },
                lines,
                rich: Views.Done(lines, s.Out.Unicode,
                    ("app id", installedAppId), ("package id", installedPackageId), ("certificate", certificate),
                    ("remove with", $"apps2samsung-cli uninstall {installedAppId} --tv {tvIp}")));
        }
        catch (ReplaceDeclinedException ex)
        {
            return s.Out.Fail($"Already on the TV: {ex.Collision.OccupantName}, under the same package id. Installing {ex.Collision.PackageName} would replace it and erase its data on the TV. Rerun with --replace to do that.", Output.Stopped);
        }
        catch (RemovalDeclinedException)
        {
            return s.Out.Fail($"The TV already has {appName} and only a remove-and-reinstall would get the new copy on; that erases the app's data on the TV. Rerun with --allow-remove to allow it.", Output.Stopped);
        }
        catch (CertificateNotYetValidException ex)
        {
            return s.Out.Fail(ex.Message, details: new { validFrom = ex.Result.ValidFromLocal });
        }
        catch (CertificateChainRejectedException ex) when (Certificates.UseTizen(s))
        {
            // The TV refused the Tizen certificate's chain: it wants a Samsung one (#988). The
            // installer's own advice (internet, clock, a new certificate) is about Samsung ones.
            return s.Out.Fail("The TV rejected the Tizen certificate ([118, -12], invalid certificate chain): this TV does not accept a Tizen certificate. "
                + (s.Options.TizenCert
                    ? "Run the install again without --tizen-cert to sign with a Samsung certificate."
                    : "Run the install again with --samsung-cert, or choose the Samsung certificate in the desktop app's Settings > Installing."),
                details: new { tizenCertificate = true, reason = ex.Reason ?? ex.Message });
        }
        catch (UserSettingsPatchException ex)
        {
            return s.Out.Fail(ex.Find is null
                ? $"{ex.PackageFile} no longer contains {ex.File}, so its settings cannot be written. The catalog needs an update."
                : $"{ex.File} in {ex.PackageFile} no longer contains '{ex.Find}', so the setting cannot be written. The catalog needs an update.");
        }
        catch (SamsungAccountEmailMissingException)
        {
            return s.Out.Fail("The Samsung account has no email address, which the certificate request needs. Add one at account.samsung.com, then retry.");
        }
        finally
        {
            if (!s.Options.Keep)
            {
                try { File.Delete(packagePath); } catch { /* best-effort */ }
            }
            else
            {
                s.Out.Progress($"Kept {packagePath}");
            }
        }
    }

    private static void RememberVariant(CliServices s, string tvIp, string? duid, string? appId, GitHubRelease release, Asset asset)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return;
        try
        {
            s.Desktop.InstalledVariantsJson = InstalledVariantStore.Set(s.Desktop.InstalledVariantsJson, new InstalledVariantRecord
            {
                TvIp = tvIp,
                TvDuid = string.IsNullOrWhiteSpace(duid) ? null : duid,
                AppId = appId,
                CatalogApp = release.Name,
                Variant = asset.Variant,
                PackageFile = asset.FileName,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                Source = release.Source.Key,
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            s.Out.Warn($"installed, but which variant could not be remembered: {ex.Message}");
        }
    }

    /// <summary>TV-Store's upload page answers, opening TV-Store on the TV first when it has to (Core).</summary>
    private static Task<bool> TvStoreReadyAsync(CliServices s, string tvIp, CancellationToken ct) =>
        TvStoreHandoff.EnsureRunningAsync(tvIp, line => s.Out.Progress(ProgressText.Localize(line, s.Strings.GetString)), ct);

    /// <summary>Does the TV answer over SDB at all (direct, or through TV-Store's relay)?</summary>
    private static async Task<bool> SdbAnswersAsync(CliServices s, string tvIp)
    {
        try
        {
            var cap = await s.Sdb.CapabilityAsync(tvIp);
            return cap.ExitCode == 0 && !string.IsNullOrWhiteSpace(cap.Output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Does the TV accept an SDB connection at all? Unlike <see cref="SdbAnswersAsync"/>, a set without the capability service still counts.</summary>
    private static async Task<bool> SdbConnectsAsync(CliServices s, string tvIp)
    {
        try
        {
            return (await s.Sdb.DevicesAsync(tvIp)).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The way in without SDB: the package, customized as for an install (the values asked before
    /// install included) but not signed, goes to TV-Store's upload page; TV-Store is brought to the
    /// front, and the person at the TV presses Install there (TV-Store signs it with its own
    /// certificate). Nothing is installed from here, so the result is the file waiting on the TV.
    /// </summary>
    private static async Task<int> SendToTvStoreAsync(CliServices s, string tvIp, string packagePath, string appName, string? version, CancellationToken ct)
    {
        if (s.Options.Launch || s.Options.RemoveFirst || s.Options.NewId || s.Options.OwnId)
            s.Out.Warn("--launch, --remove-first, --new-id and --own-id do not apply when TV-Store installs the package: ignored.");

        await s.Installer.CustomizeAsync(tvIp, packagePath, line => s.Out.Progress(ProgressText.Localize(line, s.Strings.GetString)));

        TvStoreHandoffResult sent;
        try { sent = await TvStoreHandoff.SendAsync(tvIp, packagePath, line => s.Out.Progress(ProgressText.Localize(line, s.Strings.GetString)), ct); }
        catch (TvStoreUploadException ex) { return s.Out.Fail(ex.Message); }
        var upload = sent.Upload;
        var opened = sent.TvStoreOpened;

        return s.Out.Result(
            new
            {
                ok = true,
                tv = tvIp,
                app = appName,
                via = "tvstore",
                state = "waiting",
                file = upload.Name,
                version,
                appId = upload.AppId,
                packageId = upload.PackageId,
                sha256 = upload.Sha256,
                tvStoreOpened = opened,
            },
            new[]
            {
                $"Sent {appName} to TV-Store on {tvIp}: it waits there for OK.",
                opened
                    ? "On the TV, TV-Store shows the file: press Install on this TV (TV-Store signs it with its certificate)."
                    : "Open TV-Store on the TV: it shows the file, press Install on this TV (TV-Store signs it with its certificate).",
            });
    }

    private static IReadOnlyCollection<string> SafeLocalIps(CliServices s)
    {
        try { return s.Network.GetRelevantLocalIPs().Select(a => a.ToString()).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static async Task DownloadAsync(CliServices s, string url, string dest, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(s.Options.GitHubToken) && new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Options.GitHubToken);
        using var resp = await s.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // As the desktop does: into a .part file, checked for completeness and for being a real
        // archive before it takes the package's name, so a cut-off download fails here and not
        // later in the re-sign with an unreadable zip error.
        var part = dest + ".part";
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
                await src.CopyToAsync(dst, ct);

            var expected = resp.Content.Headers.ContentLength;
            var actual = new FileInfo(part).Length;
            if (expected.HasValue && actual != expected.Value)
                throw new IOException($"Download incomplete: received {actual} of {expected.Value} bytes.");
            if (!IsValidZipArchive(part))
                throw new InvalidDataException("The downloaded package is not a valid .wgt/.tpk archive (corrupt or incomplete).");

            File.Move(part, dest, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { /* best-effort */ }
            throw;
        }
    }

    private static bool IsValidZipArchive(string path)
    {
        try
        {
            // 22 bytes is the smallest zip there is (the end-of-directory record alone).
            if (new FileInfo(path).Length < 22)
                return false;
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }
}
