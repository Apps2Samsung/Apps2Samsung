using Apps2Samsung.Catalog;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// <c>sources</c>: the sources the user added themselves (Core <see cref="UserSources"/>), kept in
/// the desktop app's settings.json so the desktop and the CLI list the same ones. <c>sources</c>
/// lists them, <c>sources add &lt;link&gt;</c> stores one, <c>sources remove &lt;link|name&gt;</c>
/// drops one. A one-off source for a single run is <c>--source &lt;link&gt;</c> on any command.
/// </summary>
public static class SourcesCommand
{
    public sealed record Entry(string Name, string Url, string Kind, string Category, bool Partner, string Fetches);

    public static Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var verb = s.Options.Arguments.Count > 0 ? s.Options.Arguments[0].ToLowerInvariant() : "list";
        return Task.FromResult(verb switch
        {
            "list" or "ls" => List(s),
            "add" => Add(s),
            "remove" or "rm" or "delete" => Remove(s),
            _ => s.Out.Fail($"unknown sources verb '{verb}'\n{Help.For("sources")}", Output.Usage),
        });
    }

    private static int List(CliServices s)
    {
        var sources = UserSources.Parse(s.Desktop.UserSourcesJson);
        var entries = sources.Select(ToEntry).ToList();
        var text = new List<string>();
        if (entries.Count == 0)
            text.Add("No sources added. `sources add <link>` adds a GitHub repository, a release page or a .wgt/.tpk link.");
        foreach (var e in entries)
        {
            text.Add($"{e.Name}  [{e.Category}]{(e.Partner ? " (partner certificate)" : "")}");
            text.Add($"    {e.Url}");
            if (!string.Equals(e.Url, e.Fetches, StringComparison.OrdinalIgnoreCase))
                text.Add($"    reads {e.Fetches}");
        }
        return s.Out.Result(new { sources = entries }, text);
    }

    private static int Add(CliServices s)
    {
        if (s.Options.Arguments.Count < 2)
            return s.Out.Fail("sources add needs the link to add", Output.Usage);
        var source = new UserSource
        {
            Url = s.Options.Arguments[1].Trim(),
            Name = s.Options.Name ?? string.Empty,
            Category = s.Options.Category ?? AppCategories.Other,
            Partner = s.Options.Partner,
        };
        var sources = UserSources.Parse(s.Desktop.UserSourcesJson);
        var errors = UserSources.Validate(source, sources);
        if (errors.TryGetValue(UserSources.FieldUrl, out var problem))
            return s.Out.Fail(problem == "errUserSourceDuplicate"
                ? "this source is already in the list"
                : "not a source the installer can read: use a GitHub repository or release link, or a direct link to a .wgt or .tpk file", Output.Usage);
        sources.Add(source);
        s.Desktop.UserSourcesJson = UserSources.Serialize(sources);
        var entry = ToEntry(source);
        return s.Out.Result(new { added = entry },
            new[] { $"Added {entry.Name} ({entry.Kind}). `catalog` lists it from now on, here and in the desktop app." });
    }

    private static int Remove(CliServices s)
    {
        if (s.Options.Arguments.Count < 2)
            return s.Out.Fail("sources remove needs the link or the name of the source", Output.Usage);
        var query = s.Options.Arguments[1].Trim();
        var sources = UserSources.Parse(s.Desktop.UserSourcesJson);
        var api = UserSources.ApiUrl(query);
        var matches = sources.Where(src =>
                (api is not null && string.Equals(UserSources.ApiUrl(src.Url), api, StringComparison.OrdinalIgnoreCase))
                || string.Equals(src.Url.Trim(), query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(UserSources.DisplayName(src), query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return s.Out.Fail($"no source matches '{query}'; `sources` lists them", Output.Usage);
        if (matches.Count > 1)
            return s.Out.Fail($"'{query}' matches {matches.Count} sources; use the link", Output.Usage,
                new { candidates = matches.Select(ToEntry).ToList() });
        sources.Remove(matches[0]);
        s.Desktop.UserSourcesJson = UserSources.Serialize(sources);
        var entry = ToEntry(matches[0]);
        return s.Out.Result(new { removed = entry }, new[] { $"Removed {entry.Name}. Apps installed from it stay on the TV." });
    }

    public static Entry ToEntry(UserSource source) => new(
        UserSources.DisplayName(source),
        source.Url.Trim(),
        UserSources.Classify(source.Url) switch
        {
            UserSourceKind.GitHubReleases => "github repository",
            UserSourceKind.GitHubReleaseTag => "github release",
            UserSourceKind.DirectFile => "file link",
            _ => "unreadable",
        },
        AppCategories.Normalize(source.Category),
        source.Partner,
        UserSources.ApiUrl(source.Url) ?? string.Empty);
}
