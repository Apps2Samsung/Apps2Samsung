using System.IO.Compression;
using System.Net.Http.Headers;
using Apps2Samsung.Catalog;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Install;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Services;

namespace Apps2Samsung.Cli.Commands;

public static class InstallCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var target = s.Options.Arguments.FirstOrDefault();
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli install <app|file> --tv <ip>   (`install --help` for the options)", Output.Usage);

        // ---- What to install: a local file, or a catalog app and one of its files ----
        GitHubRelease? release = null;
        Asset? asset = null;
        string? localFile = null;
        if (File.Exists(target) && (target.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
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
            var (match, candidates) = CatalogLoader.Find(visible, target);
            if (match is null)
            {
                return candidates.Count == 0
                    ? s.Out.Fail($"No catalog app matches '{target}'. `apps2samsung-cli catalog {target}` searches descriptions too.")
                    : s.Out.Fail($"'{target}' matches several apps; use the exact name: {string.Join(" | ", candidates.Select(c => c.Name))}",
                        details: new { candidates = candidates.Select(c => c.Name) });
            }
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

            // A package that gains root: only when asked for by name, as the apps ask first.
            if (release.IsRoot && !s.Options.AllowRoot)
                return s.Out.Fail(
                    $"{release.Name} gains root on the TV and runs a background service from boot. After the install, set Developer Mode's Host PC IP to 127.0.0.1 and restart the TV; " +
                    "until it is set back to this machine, nothing can reach the TV over SDB (updates and other installs included). Rerun with --allow-root to install it.",
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
        var localIp = LocalIp.Of(s);
        var guards = InstallGuards.Evaluate(device, new InstallGuardOptions
        {
            LocalIps = SafeLocalIps(s),
            ConfiguredLocalIp = localIp,
        }, s.Network);
        foreach (var g in guards.Guards)
            s.Out.Warn($"{g.DefaultTitle}: {g.DefaultMessageWithDetail.Replace("\n\n", " ")}");
        if (guards.Guards.Count > 0 && !s.Options.Yes)
            return s.Out.Fail($"Stopped before installing: {guards.Guards.Count} check(s) above would most likely make it fail. Fix them, or rerun with --yes to try anyway.",
                Output.Stopped, new { checks = guards.Guards.Select(g => new { kind = g.Kind.ToString(), g.DefaultTitle, message = g.DefaultMessageWithDetail }) });
        var tvIp = guards.CorrectedTvIp ?? ip;

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
        }

        try
        {
            // ---- Certificate ----
            // The desktop's Automatic / Public / Partner setting, unless --partner; a package that
            // needs Partner gets it either way.
            var needsPartner = (release?.RequiresPartner ?? false) || WgtPrivileges.RequiresPartner(packagePath);
            var preference = Certificates.Preference(s);
            var level = CertificatePreference.ResolveLevel(preference, needsPartner);
            if (needsPartner && CertificatePreference.ResolveLevel(preference, false) != CertificatePrivilegeLevel.Partner)
                s.Out.Progress("This package needs a Partner-level certificate; signing as Partner.");
            var (profile, duid, _) = await Certificates.EnsureAsync(s, tvIp, level, ct);

            // ---- Install ----
            var (appId, packageId) = await WgtManifest.ReadIdsAsync(packagePath);
            var lastPercent = -1;
            var output = await s.Installer.InstallAsync(tvIp, packagePath, profile,
                progress: line =>
                {
                    // One line per percentage step would flood a log; every tenth is enough.
                    if (line.StartsWith("Installing on TV… ", StringComparison.Ordinal) && line.EndsWith('%'))
                    {
                        if (!int.TryParse(line[18..^1], out var pct) || (pct / 10 == lastPercent / 10 && pct != 100))
                            return;
                        lastPercent = pct;
                    }
                    s.Out.Progress(line);
                },
                stage: null,
                ct: ct,
                confirmRemove: () => Task.FromResult(s.Options.AllowRemove),
                packageId: new PackageIdPolicy(duid, s.Options.NewId));

            // The ids as installed: a rename (#839) changed them after the read above.
            var (installedAppId, installedPackageId) = await WgtManifest.ReadIdsAsync(packagePath);
            var renamed = !string.Equals(installedPackageId, packageId, StringComparison.Ordinal);
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
                    certificate = level.ToString(),
                    duid,
                    launched,
                },
                release?.IsRoot == true
                    ? new[]
                    {
                        $"Installed {appName} on {tvIp}{(renamed ? $" under package id {installedPackageId} (remembered for this TV)" : "")}{(launched ? " and launched it" : "")}.",
                        "Now set Developer Mode's Host PC IP to 127.0.0.1 and restart the TV. Until it is set back to this machine, nothing can reach the TV over SDB.",
                    }
                    : new[] { $"Installed {appName} on {tvIp}{(renamed ? $" under package id {installedPackageId} (remembered for this TV)" : "")}{(launched ? " and launched it" : "")}." });
        }
        catch (RemovalDeclinedException)
        {
            return s.Out.Fail($"The TV already has {appName} and only a remove-and-reinstall would get the new copy on; that erases the app's data on the TV. Rerun with --allow-remove to allow it.", Output.Stopped);
        }
        catch (CertificateNotYetValidException ex)
        {
            return s.Out.Fail(ex.Message, details: new { validFrom = ex.Result.ValidFromLocal });
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
