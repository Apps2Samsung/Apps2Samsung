using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

public static class InstalledCommand
{
    public sealed record Row(string Title, string AppId, string? Version, string InstallDate, bool Removable,
        string? CatalogApp, string? CatalogFile, string? CatalogVersion, string? State);

    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Tv ?? s.Options.Arguments.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli installed --tv <ip>", Output.Usage);

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

        var rows = apps.Select(a =>
        {
            var match = catalog.Count == 0 ? null : InstalledAppUpdates.Find(a, catalog, s.Config.PackageIdRenamesJson);
            return new Row(a.DisplayName, a.TizenId, string.IsNullOrWhiteSpace(a.Version) ? null : a.Version, a.InstallDate, a.IsRemovable,
                match?.Release.Name, match?.Asset.FileName, match?.CatalogVersion,
                match is null ? null : match.State switch
                {
                    CatalogVersionState.UpToDate => "up-to-date",
                    CatalogVersionState.UpdateAvailable => "update-available",
                    CatalogVersionState.Different => "different",
                    _ => "unknown",
                });
        }).ToList();

        var text = new List<string>();
        if (rows.Count == 0)
            text.Add("The TV lists no apps.");
        foreach (var r in rows)
        {
            var version = r.Version is null ? "" : $" v{r.Version}";
            var cat = r.CatalogApp is null ? "" : $"  catalog: {r.CatalogApp}{(r.CatalogVersion is null ? "" : $" v{r.CatalogVersion}")} ({r.State})";
            text.Add($"{r.Title}{version}  [{r.AppId}]{cat}");
        }
        return s.Out.Result(new { ok = true, tv = ip, count = rows.Count, apps = rows }, text);
    }
}
