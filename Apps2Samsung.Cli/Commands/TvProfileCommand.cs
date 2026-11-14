using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// `tv save <file> --tv <ip>` and `tv restore <file> --tv <ip>` (#1069). Save writes down the apps on
/// a TV that came from the catalog or a source; restore installs them again on a TV that lost them
/// (factory reset, Smart Hub reset, a new set), one after the other, each exactly as `install` would.
/// </summary>
public static class TvProfileCommand
{
    private const string UsageText =
        "usage: apps2samsung-cli tv save <file> --tv <ip> [--with-settings]\n" +
        "       apps2samsung-cli tv restore <file> --tv <ip> [app...] [install options]";

    public static Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var verb = s.Options.Arguments[0].ToLowerInvariant();
        var file = s.Options.Arguments.ElementAtOrDefault(1);
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(ip))
            return Task.FromResult(s.Out.Fail(UsageText, Output.Usage));
        return verb == "save" ? SaveAsync(s, file, ip, ct) : RestoreAsync(s, file, ip, ct);
    }

    private static async Task<int> SaveAsync(CliServices s, string file, string ip, CancellationToken ct)
    {
        var path = Path.GetFullPath(file);
        if (File.Exists(path) && !s.Options.Yes)
            return s.Out.Fail($"{path} exists already. Pick another name, or add --yes to write over it.", Output.Usage);

        s.Out.Progress($"Reading the apps on {ip}…");
        var apps = TizenInstalledApps.ParseOrThrow(await s.Sdb.AppsAsync(ip));
        // Without the catalog there is nothing to say which apps can be installed again.
        s.Out.Progress("Matching them to the catalog…");
        var catalog = await s.Catalog.LoadAsync(false, ct);

        string? name = null, model = null;
        try
        {
            var device = await s.Network.ValidateManualTizenAddress(ip, ct);
            name = device?.DeviceName;
            model = device?.ModelName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The name only labels the file.
        }

        var settings = s.Desktop.AppUserSettingsJson;
        TvProfileSaved Build(bool withSettings) => TvProfiles.Save(apps, catalog.Entries, catalog.RemovedApps,
            s.Config.PackageIdRenamesJson, Variants(s), ip, null, settings, withSettings, DateTimeOffset.UtcNow, name, model);

        var saved = Build(s.Options.WithSettings);
        var profile = saved.Profile;
        if (profile.Apps.Count == 0)
            return s.Out.Fail($"None of the {apps.Count} apps on {ip} is in the catalog or a source, so there is nothing to save.",
                details: new { tv = ip, leftOut = saved.LeftOut });

        await File.WriteAllTextAsync(path, TvProfiles.Serialize(profile), ct);

        var lines = new List<string> { $"Saved {profile.Apps.Count} app(s) on {ip} to {path}." };
        lines.AddRange(profile.Apps.Select(a => $"  {a.Name}{(a.Variant is null ? "" : $" {a.Variant}")}{(a.Version is null ? "" : $" v{a.Version}")}"
            + (a.Uncertain ? "  (which of its files the TV has is a guess: this machine did not install it)" : "")
            + (a.Settings is null ? "" : "  (with its values)")));
        var removed = saved.LeftOut.Where(l => l.Reason == TvProfileLeftOutReason.RemovedFromCatalog).ToList();
        if (removed.Count > 0)
            lines.Add($"Left out, removed from the catalogue: {string.Join(", ", removed.Select(l => l.Title))}.");
        var unknown = saved.LeftOut.Count - removed.Count;
        if (unknown > 0)
            lines.Add($"Left out: {unknown} app(s) that are not in the catalog or a source (preinstalled, from Samsung's store, or installed from a file).");

        if (profile.ContainsSettings)
            lines.Add("The file holds the values typed for these apps, which can be server addresses and API keys. Keep it private.");
        else if (!s.Options.WithSettings && Build(true).Profile.Apps.Count(a => a.Settings is not null) is > 0 and var withValues)
            lines.Add($"{withValues} app(s) have values saved on this machine (server address, API key). They are not in the file; add --with-settings to include them.");

        return s.Out.Result(new { ok = true, tv = ip, file = path, count = profile.Apps.Count, profile, leftOut = saved.LeftOut }, lines);
    }

    private static async Task<int> RestoreAsync(CliServices s, string file, string ip, CancellationToken ct)
    {
        var path = Path.GetFullPath(file);
        if (!File.Exists(path))
            return s.Out.Fail($"There is no file at {path}.", Output.Usage);
        TvProfile profile;
        try
        {
            profile = TvProfiles.Parse(await File.ReadAllTextAsync(path, ct));
        }
        catch (FormatException ex)
        {
            return s.Out.Fail($"{path}: {ex.Message}");
        }

        // Names after the file narrow the restore to those apps.
        var names = s.Options.Arguments.Skip(2).ToList();
        var notInProfile = names.Where(n => !profile.Apps.Any(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (notInProfile.Count > 0)
            return s.Out.Fail($"Not in this profile: {string.Join(", ", notInProfile)}. It holds: {string.Join(", ", profile.Apps.Select(a => a.Name))}.", Output.Usage);
        if (names.Count > 0)
            profile.Apps.RemoveAll(a => !names.Any(n => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)));

        s.Out.Progress($"Reading the apps on {ip}…");
        var onTv = TizenInstalledApps.ParseOrThrow(await s.Sdb.AppsAsync(ip));
        s.Out.Progress("Finding the profile's apps in the catalog…");
        var catalog = await s.Catalog.LoadAsync(false, ct);
        var plan = TvProfiles.Plan(profile, catalog.Entries, onTv, s.Config.PackageIdRenamesJson, Variants(s), ip);

        var results = new List<object>();
        var lines = new List<string>();
        int installed = 0, failed = 0, skipped = 0;
        var ready = plan.Where(p => p.State == TvRestoreState.Ready).ToList();
        var done = 0;
        foreach (var item in plan)
        {
            var app = item.App;
            if (item.State == TvRestoreState.AlreadyOnTv)
            {
                skipped++;
                lines.Add($"{app.Name}: already on the TV");
                results.Add(new { app = app.Name, state = "already-on-tv", ok = true });
                continue;
            }
            if (item.State == TvRestoreState.NotInCatalog || item.Release is null || item.Asset is null)
            {
                failed++;
                var where = app.Source == "official" ? "the catalog" : $"the source it came from ({app.Source})";
                lines.Add($"{app.Name}: no longer in {where}" + (app.Source == "official" ? "" : "; add that source with `sources add` and run the restore again"));
                results.Add(new { app = app.Name, state = "not-in-catalog", ok = false });
                continue;
            }

            // Values the package needs (#791): asked for before the run starts on it, so one app
            // without them does not look like a failed install.
            var values = UpdateCommand.SettingValues(s, item.Asset, app.Settings);
            var missing = (item.Asset.UserSettings?.Fields ?? new List<UserSettingsField>())
                .Where(f => UserSettingsStore.Validate(f, values.FirstOrDefault(v => string.Equals(v.Key, f.Key, StringComparison.OrdinalIgnoreCase)).Value) is not null)
                .ToList();
            if (missing.Count > 0)
            {
                failed++;
                var sets = string.Join(" ", missing.Select(f => $"--set {f.Key}=<{f.Type}>"));
                lines.Add($"{app.Name}: needs values this profile does not hold. Install it with: apps2samsung-cli install \"{app.Name}\" --tv {ip} --file {item.Asset.FileName} {sets}");
                results.Add(new { app = app.Name, state = "needs-values", ok = false, settings = missing.Select(f => new { f.Key, f.Label, f.Type }) });
                continue;
            }

            done++;
            s.Out.Progress($"Installing {done} of {ready.Count}: {app.Name}{(item.Asset.PackageVersion is null ? "" : $" {item.Asset.PackageVersion}")}");
            if (item.VariantChanged)
                s.Out.Warn($"{app.Name}: the catalog no longer lists {app.Variant ?? app.PackageFile}; installing {item.Asset.FileName} instead.");
            var args = UpdateCommand.InstallLine(s, ip, item.Release.Name, item.Asset, SourcesCommand.FromValue(item.Release.Source), app.Settings);
            int exit;
            JsonElement? answer = null;
            if (s.Out.AsJson)
            {
                // Each install answers with its own JSON document; this keeps them for the one summary.
                var captured = new StringWriter();
                using (Output.CaptureStdOut(captured))
                    exit = await Dispatch.RunLineAsync(args, ct, s.Sdb);
                try { answer = JsonDocument.Parse(captured.ToString()).RootElement.Clone(); } catch (JsonException) { }
            }
            else
            {
                exit = await Dispatch.RunLineAsync(args, ct, s.Sdb);
            }

            if (exit == Output.Ok) installed++; else failed++;
            lines.Add($"{app.Name}: {(exit == Output.Ok ? $"installed{(item.Asset.PackageVersion is null ? "" : $" {item.Asset.PackageVersion}")}" : "failed (see above)")}");
            results.Add(new { app = app.Name, state = exit == Output.Ok ? "installed" : "failed", ok = exit == Output.Ok, exit, file = item.Asset.FileName, install = answer });
        }

        var summary = $"{installed} installed on {ip}";
        if (skipped > 0) summary += $", {skipped} already there";
        if (failed > 0) summary += $", {failed} not installed";
        lines.Insert(0, summary + ".");
        return s.Out.Result(new { ok = failed == 0, tv = ip, file = path, installed, skipped, failed, apps = results }, lines,
            failed == 0 ? Output.Ok : Output.Failed);
    }

    // The installed variants are the desktop's list; a settings file that cannot be read just means each app's own match.
    private static string? Variants(CliServices s)
    {
        try { return s.Desktop.InstalledVariantsJson; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }
}
