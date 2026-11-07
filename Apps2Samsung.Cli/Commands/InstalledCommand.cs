using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

public static class InstalledCommand
{
    /// <param name="CatalogVariant">Which of the app's files the comparison is with, when it has several: the
    /// one this machine installed on this TV (<paramref name="VariantRemembered"/>), else the catalog's pick.</param>
    public sealed record Row(string Title, string AppId, string? Version, string InstallDate, bool Removable,
        string? CatalogApp, string? CatalogFile, string? CatalogVersion, string? State,
        string? CatalogVariant = null, bool VariantRemembered = false);

    /// <summary>The `install` line that brings the row's catalog app to the TV: with --file when the app has several files (#975).</summary>
    public static List<string> InstallArgs(Row r, string ip)
    {
        var args = new List<string> { "install", r.CatalogApp ?? string.Empty, "--tv", ip };
        if (r.CatalogVariant is not null)
            args.AddRange(new[] { "--file", r.CatalogVariant });
        return args;
    }

    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Tv ?? s.Options.Arguments.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli installed --tv <ip>", Output.Usage);

        var rows = await ReadAsync(s, ip, ct);
        var text = new List<string>();
        if (rows.Count == 0)
            text.Add("The TV lists no apps.");
        foreach (var r in rows)
        {
            var version = r.Version is null ? "" : $" v{r.Version}";
            var variant = r.CatalogVariant is null ? "" : $" {r.CatalogVariant}";
            var cat = r.CatalogApp is null ? "" : $"  catalog: {r.CatalogApp}{variant}{(r.CatalogVersion is null ? "" : $" v{r.CatalogVersion}")} ({r.State})";
            text.Add($"{r.Title}{version}  [{r.AppId}]{cat}");
        }
        return s.Out.Result(new { ok = true, tv = ip, count = rows.Count, apps = rows }, text,
            rich: Views.InstalledApps(rows, ip, s.Out.Width, s.Out.Unicode));
    }

    /// <summary>The apps on the TV with the catalog comparison, for the listing and for the menu.</summary>
    public static async Task<List<Row>> ReadAsync(CliServices s, string ip, CancellationToken ct)
    {
        s.Out.Progress($"Reading the apps on {ip}…");
        var result = await s.Sdb.AppsAsync(ip);
        var apps = TizenInstalledApps.ParseOrThrow(result);

        // The catalog comparison is best-effort: the list is still useful without network.
        IReadOnlyList<GitHubRelease> catalog = Array.Empty<GitHubRelease>();
        try
        {
            s.Out.Progress("Comparing with the catalog…");
            catalog = (await s.Catalog.LoadAsync(false, ct)).Entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            s.Out.Warn($"catalog not available, versions not compared: {ex.Message}");
        }

        string? variants = null;
        try { variants = s.Desktop.InstalledVariantsJson; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            s.Out.Warn($"the installed variants could not be read, comparing with each app's default file: {ex.Message}");
        }

        var matches = apps.Select(a => catalog.Count == 0 ? null : InstalledAppUpdates.Find(a, catalog, s.Config.PackageIdRenamesJson, variants, ip)).ToList();
        // Apps that moved to their author's repository (#1018): the command that adds it, with the key the official catalog names.
        foreach (var hint in Apps2Samsung.Catalog.Repositories.MovedApps.Hints(matches, UserSources.Parse(s.Desktop.UserSourcesJson)))
            s.Out.Warn($"{string.Join(", ", hint.AppNames)} moved to its author's repository at {hint.Domain}. Keep getting updates with: " +
                $"apps2samsung-cli sources add {hint.To.Url} --fingerprint {hint.To.Fingerprint}");

        return apps.Select((a, i) =>
        {
            // The variant this TV got last time (#975) decides which file an app with several is compared with.
            var match = matches[i];
            return new Row(a.DisplayName, a.TizenId, string.IsNullOrWhiteSpace(a.Version) ? null : a.Version, a.InstallDate, a.IsRemovable,
                match?.Release.Name, match?.Asset.FileName, match?.CatalogVersion,
                match is null ? null : match.State switch
                {
                    CatalogVersionState.UpToDate => "up-to-date",
                    CatalogVersionState.UpdateAvailable => "update-available",
                    CatalogVersionState.Different => "different",
                    _ => "unknown",
                },
                match is { Release.Assets.Count: > 1 } ? match.Asset.VariantLabel : null,
                match?.VariantRemembered ?? false);
        }).ToList();
    }
}
