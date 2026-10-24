using System.Net.Http.Headers;
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
            var (match, candidates) = CatalogLoader.Find(catalog.Entries, target);
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
                var versions = catalog.Entries.Where(e => string.Equals(e.Name, match.Name, StringComparison.OrdinalIgnoreCase)).ToList();
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

            // Values the package needs before it installs (#791).
            if (asset.UserSettings is { Fields.Count: > 0 } spec)
            {
                var problems = new List<string>();
                foreach (var field in spec.Fields)
                {
                    s.Options.Settings.TryGetValue(field.Key, out var value);
                    var key = UserSettingsStore.Validate(field, value);
                    if (key is not null)
                        problems.Add($"--set {field.Key}=<{field.Type}>  {field.Label}: {s.Strings.GetString(key)}");
                }
                if (problems.Count > 0)
                    return s.Out.Fail($"{release.Name} needs values before it installs{(spec.HelpUrl is null ? "" : $" (see {spec.HelpUrl})")}:\n  {string.Join("\n  ", problems)}",
                        details: new { settings = spec.Fields.Select(f => new { f.Key, f.Label, f.Type, f.Required, f.Pattern }), spec.HelpUrl });
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

        // ---- Download ----
        string packagePath;
        var downloaded = false;
        if (localFile is not null)
        {
            packagePath = localFile;
        }
        else
        {
            Directory.CreateDirectory(CliPaths.Downloads);
            packagePath = Path.Combine(CliPaths.Downloads, asset!.FileName);
            s.Out.Progress($"Downloading {asset.FileName} ({asset.DisplayText})…");
            await DownloadAsync(s, asset.DownloadUrl, packagePath, ct);
            downloaded = true;
        }

        try
        {
            // ---- Certificate ----
            var needsPartner = s.Options.Partner || (release?.RequiresPartner ?? false) || WgtPrivileges.RequiresPartner(packagePath);
            if (needsPartner && !s.Options.Partner)
                s.Out.Progress("This package needs a Partner-level certificate; signing as Partner.");
            var level = needsPartner ? CertificatePrivilegeLevel.Partner : CertificatePrivilegeLevel.Public;
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
                confirmRemove: () => Task.FromResult(s.Options.Yes));

            var launched = s.Config.OpenAfterInstall && !string.IsNullOrWhiteSpace(appId);
            return s.Out.Result(
                new
                {
                    ok = true,
                    tv = tvIp,
                    app = appName,
                    file = Path.GetFileName(packagePath),
                    version = asset?.PackageVersion,
                    appId,
                    packageId,
                    certificate = level.ToString(),
                    duid,
                    launched,
                },
                new[] { $"Installed {appName} on {tvIp}{(launched ? " and launched it" : "")}." });
        }
        catch (RemovalDeclinedException)
        {
            return s.Out.Fail($"The TV already has {appName} and only a remove-and-reinstall would get the new copy on; that erases the app's data on the TV. Rerun with --yes to allow it.", Output.Stopped);
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
            if (downloaded && !s.Options.Keep)
            {
                try { File.Delete(packagePath); } catch { /* best-effort */ }
            }
            else if (downloaded)
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
        try
        {
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct);
        }
        catch
        {
            try { File.Delete(dest); } catch { /* best-effort */ }
            throw;
        }
    }
}
