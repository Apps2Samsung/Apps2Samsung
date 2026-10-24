using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Cli.Commands;

public static class CatalogCommand
{
    public sealed record FileInfo(string File, string? Variant, string? Version, long Size, bool Default, IReadOnlyList<SettingInfo>? Settings);
    public sealed record SettingInfo(string Key, string Label, string Type, bool Required, string? Pattern, string? HelpUrl);
    public sealed record Entry(string Name, string Category, string Description, bool RequiresPartner, bool HasVariants,
        string? Tag, string? Published, string? Author, string? Source, IReadOnlyList<FileInfo> Files);

    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        s.Out.Progress("Loading the app catalog…");
        var catalog = await s.Catalog.LoadAsync(s.Options.AllVersions, ct);
        if (catalog.FailedProviders > 0)
            s.Out.Warn($"{catalog.FailedProviders} source(s) could not be read; their apps are missing from this list");

        IEnumerable<GitHubRelease> entries = catalog.Entries;
        if (!string.IsNullOrWhiteSpace(s.Options.Category))
        {
            var id = AppCategories.Normalize(s.Options.Category);
            entries = CommunityAppList.Filter(entries, id);
        }
        var words = s.Options.Arguments;
        if (words.Count > 0)
            entries = entries.Where(e => words.All(w =>
                e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(w, StringComparison.OrdinalIgnoreCase)));

        var list = entries.Select(ToEntry).ToList();
        var text = new List<string>();
        if (list.Count == 0)
            text.Add("No app matches.");
        foreach (var e in list)
        {
            var flags = e.RequiresPartner ? " (partner certificate)" : "";
            text.Add($"{e.Name}  [{e.Category}]{flags}");
            if (e.Description.Length > 0)
                text.Add($"    {e.Description}");
            if (e.Files.Count > 1 || e.Files.Any(f => f.Settings is not null))
            {
                foreach (var f in e.Files)
                {
                    var label = f.Variant is not null ? $"{f.Variant}: {f.File}" : f.File;
                    var version = f.Version is not null ? $" v{f.Version}" : "";
                    text.Add($"    - {label}{version}{(f.Default ? " (default)" : "")}");
                    if (f.Settings is not null)
                        foreach (var st in f.Settings)
                            text.Add($"        --set {st.Key}=…  {st.Label}{(st.Required ? "" : " (optional)")}{(st.HelpUrl is null ? "" : $"  see {st.HelpUrl}")}");
                }
            }
            else if (e.Files.Count == 1)
            {
                // The package's own version when the catalog knows it, else the release tag.
                var version = e.Files[0].Version is not null ? $"v{e.Files[0].Version}" : e.Tag;
                if (version is not null)
                    text.Add($"    {version}");
            }
        }
        if (list.Count > 0)
            text.Add($"{list.Count} app(s). Install one with: apps2samsung-cli install \"<name>\" --tv <ip>");
        return s.Out.Result(new { ok = true, count = list.Count, apps = list }, text);
    }

    public static Entry ToEntry(GitHubRelease r)
    {
        var files = r.Assets.Select(a => new FileInfo(
            a.FileName,
            string.IsNullOrWhiteSpace(a.Variant) ? null : a.Variant,
            a.PackageVersion,
            a.Size,
            a.IsDefault || (r.Assets.Count == 1),
            a.UserSettings is null ? null : a.UserSettings.Fields.Select(f =>
                new SettingInfo(f.Key, f.Label, f.Type, f.Required, f.Pattern, a.UserSettings.HelpUrl)).ToList())).ToList();
        var published = DateTime.TryParse(r.PublishedAt, out var date) ? date.ToString("yyyy-MM-dd") : null;
        return new Entry(r.Name, r.Category, r.Description, r.RequiresPartner, r.HasVariants,
            string.IsNullOrWhiteSpace(r.TagName) ? null : r.TagName, published, r.Author, r.SourceUrl, files);
    }
}
