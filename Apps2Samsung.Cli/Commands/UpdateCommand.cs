using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// `update --tv <ip> --all` or `update <app>... --tv <ip>` (#1067): installs the catalog's newer version
/// of apps on the TV, one after the other, each exactly as `install` would (same variant, same source,
/// the values the desktop saved for a package that asks for some), and ends with one summary. --all takes
/// the apps whose catalog version is clearly higher; an app whose versions differ but cannot be ordered
/// is only updated when named.
/// </summary>
public static class UpdateCommand
{
    private sealed record Target(string Name, GitHubRelease Release, Asset Asset, string? From, string? Installed);

    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Tv;
        var names = s.Options.Arguments.ToList();
        if (string.IsNullOrWhiteSpace(ip) || (!s.Options.All && names.Count == 0))
            return s.Out.Fail("usage: apps2samsung-cli update --tv <ip> --all   or   update <app>... --tv <ip>", Output.Usage);

        s.Out.Progress($"Reading the apps on {ip}…");
        var apps = TizenInstalledApps.ParseOrThrow(await s.Sdb.AppsAsync(ip));
        s.Out.Progress("Comparing with the catalog…");
        var catalog = await s.Catalog.LoadAsync(false, ct);
        var removed = catalog.RemovedApps;
        string? variants = null;
        try { variants = s.Desktop.InstalledVariantsJson; } catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }

        var targets = new List<Target>();
        var notFound = new List<string>();
        foreach (var app in apps)
        {
            if (RemovedApps.Find(app, removed, catalog.Entries, s.Config.PackageIdRenamesJson) is not null)
                continue;
            if (InstalledAppUpdates.Find(app, catalog.Entries, s.Config.PackageIdRenamesJson, variants, ip) is not { } match)
                continue;
            var named = names.Any(n => string.Equals(n, match.Release.Name, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(n, app.DisplayName, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(n, app.TizenId, StringComparison.OrdinalIgnoreCase));
            var wanted = named ? match.HasUpdate : s.Options.All && match.State == CatalogVersionState.UpdateAvailable;
            if (wanted)
                targets.Add(new Target(match.Release.Name, match.Release, match.Asset, SourcesCommand.FromValue(match.Release.Source),
                    string.IsNullOrWhiteSpace(app.Version) ? null : app.Version));
        }
        foreach (var n in names)
            if (!targets.Any(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase)))
                notFound.Add(n);

        if (targets.Count == 0)
            return s.Out.Result(new { ok = true, tv = ip, updated = Array.Empty<object>(), notFound },
                new[] { notFound.Count > 0 ? $"No update for: {string.Join(", ", notFound)}." : $"Every app on {ip} is up to date with the catalog." });

        var results = new List<object>();
        var lines = new List<string>();
        var failed = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            s.Out.Progress($"Updating {i + 1} of {targets.Count}: {t.Name} {t.Installed} → {t.Asset.PackageVersion}");
            var args = InstallArgs(s, ip, t);
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

            if (exit != Output.Ok)
                failed++;
            lines.Add($"{t.Name}: {(exit == Output.Ok ? $"updated to {t.Asset.PackageVersion}" : "failed (see above)")}");
            results.Add(new { app = t.Name, from = t.Installed, to = t.Asset.PackageVersion, ok = exit == Output.Ok, exit, install = answer });
        }

        lines.Insert(0, failed == 0 ? $"{targets.Count - failed} updated on {ip}." : $"{targets.Count - failed} updated, {failed} failed on {ip}.");
        if (notFound.Count > 0)
            lines.Add($"No update for: {string.Join(", ", notFound)}.");
        return s.Out.Result(new { ok = failed == 0, tv = ip, updated = results, notFound }, lines, failed == 0 ? Output.Ok : Output.Failed);
    }

    // The `install` line for one app, with the options of this run that matter to an install.
    private static List<string> InstallArgs(CliServices s, string ip, Target t)
    {
        var o = s.Options;
        var args = new List<string> { "install", t.Name, "--tv", ip, "--file", t.Asset.FileName };
        if (t.From is not null) args.AddRange(new[] { "--from", t.From });
        if (o.Json) args.Add("--json");
        if (o.Quiet) args.Add("--quiet");
        if (o.Plain) args.Add("--plain");
        if (o.DataDir is not null) args.AddRange(new[] { "--data-dir", o.DataDir });
        if (o.LocalIp is not null) args.AddRange(new[] { "--local-ip", o.LocalIp });
        if (o.Yes) args.Add("--yes");
        if (o.Partner) args.Add("--partner");
        if (o.SamsungCert) args.Add("--samsung-cert");
        if (o.TizenCert) { args.Add("--tizen-cert"); if (o.TizenLevel is not null) args.Add(o.TizenLevel); }
        if (o.AllowRemove) args.Add("--allow-remove");
        if (o.AllowRoot) args.Add("--allow-root");
        if (o.SkipDevModeCheck) args.Add("--skip-devmode-check");
        if (o.RemoveFirst) args.Add("--remove-first");
        if (o.Keep) args.Add("--keep");
        if (o.Adult) args.Add("--adult");

        // Values the package asks for: --set on this line first, else what the desktop saved (#791).
        if (t.Asset.UserSettings is { Fields.Count: > 0 } spec)
        {
            var saved = UserSettingsStore.Get(s.Desktop.AppUserSettingsJson, t.Asset.FileName)?.Values;
            foreach (var field in spec.Fields)
            {
                var value = o.Settings.TryGetValue(field.Key, out var given) ? given
                    : saved is not null && saved.TryGetValue(field.Key, out var kept) ? kept : null;
                if (value is not null)
                    args.AddRange(new[] { "--set", $"{field.Key}={value}" });
            }
        }
        return args;
    }
}
