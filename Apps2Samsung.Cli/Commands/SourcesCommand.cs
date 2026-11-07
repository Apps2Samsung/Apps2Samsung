using Apps2Samsung.Catalog;
using Apps2Samsung.Catalog.Repositories;
using Apps2Samsung.Models;
using Spectre.Console;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// <c>sources</c>: the sources the user added themselves (Core <see cref="UserSources"/>), kept in
/// the desktop app's settings.json so the desktop and the CLI list the same ones. <c>sources</c>
/// lists them, <c>sources add &lt;link&gt;</c> stores one, <c>sources remove &lt;link|name&gt;</c>
/// drops one. A one-off source for a single run is <c>--source &lt;link&gt;</c> on any command.
/// </summary>
public static class SourcesCommand
{
    /// <param name="RepositoryId">A signed repository's pinned id (#1018); null for the unverified kinds.</param>
    /// <param name="Fingerprint">The approved key of a signed repository.</param>
    public sealed record Entry(string Name, string Url, string Kind, string Category, bool Partner, string Fetches,
        string? RepositoryId = null, string? Fingerprint = null);

    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var verb = s.Options.Arguments.Count > 0 ? s.Options.Arguments[0].ToLowerInvariant() : "list";
        return verb switch
        {
            "list" or "ls" => List(s),
            "add" => await AddAsync(s, ct),
            "remove" or "rm" or "delete" => Remove(s),
            _ => s.Out.Fail($"unknown sources verb '{verb}'\n{Help.For("sources")}", Output.Usage),
        };
    }

    // ---- provenance (#1018), shared with catalog and install ----

    /// <summary>"Official", "Repository: name" or "Unverified: name", as every head labels an app.</summary>
    public static string Label(CatalogSource source) => source.Kind switch
    {
        CatalogSourceKind.Official => "Official",
        CatalogSourceKind.Repository => $"Repository: {source.Name}",
        _ => $"Unverified: {source.Name}",
    };

    /// <summary>What to pass to --from for this source.</summary>
    public static string FromValue(CatalogSource source) => source.Kind switch
    {
        CatalogSourceKind.Official => "official",
        CatalogSourceKind.Repository => source.RepositoryId ?? source.Name,
        _ => source.Name,
    };

    /// <summary>Whether --from <paramref name="from"/> names <paramref name="source"/>: "official", a repository id, or a source's name.</summary>
    public static bool Matches(CatalogSource source, string from)
    {
        var f = from.Trim();
        return source.Kind == CatalogSourceKind.Official
            ? string.Equals(f, "official", StringComparison.OrdinalIgnoreCase)
            : string.Equals(f, source.Name, StringComparison.OrdinalIgnoreCase)
              || string.Equals(f, source.RepositoryId, StringComparison.OrdinalIgnoreCase);
    }

    private static int List(CliServices s)
    {
        var sources = UserSources.Parse(s.Desktop.UserSourcesJson);
        var entries = sources.Select(ToEntry).ToList();
        var text = new List<string>();
        if (entries.Count == 0)
            text.Add("No sources added. `sources add <link>` adds a GitHub repository, a release page, a .wgt/.tpk link or a .json feed.");
        foreach (var e in entries)
        {
            if (e.RepositoryId is not null)
            {
                text.Add($"{e.Name}  (signed repository {e.RepositoryId})");
                text.Add($"    {e.Url}");
                text.Add($"    key {RepositoryVerifier.FormatFingerprint(e.Fingerprint!)}");
                continue;
            }
            text.Add($"{e.Name}  [{e.Category}]{(e.Partner ? " (partner certificate)" : "")}  unverified");
            text.Add($"    {e.Url}");
            if (!string.Equals(e.Url, e.Fetches, StringComparison.OrdinalIgnoreCase))
                text.Add($"    reads {e.Fetches}");
        }
        return s.Out.Result(new { sources = entries }, text, rich: Views.Sources(text, s.Out.Unicode));
    }

    private static async Task<int> AddAsync(CliServices s, CancellationToken ct)
    {
        if (s.Options.Arguments.Count < 2)
            return s.Out.Fail("sources add needs the link to add", Output.Usage);
        if (UserSources.IsSignedRepository(s.Options.Arguments[1]))
            return await AddRepositoryAsync(s, s.Options.Arguments[1].Trim(), ct);
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
                : "not a source the installer can read: use a GitHub repository or release link, a direct link to a .wgt or .tpk file, a link to a .json feed, or an https link to a signed repository's repository.json.signed", Output.Usage);
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

    /// <summary>
    /// A signed repository (#1018): read it and its key, show who publishes it and the key's
    /// fingerprint, and store it only once that key is approved: at the prompt on a terminal, or by
    /// --fingerprint (that key and no other) or --yes. Adding a repository that is already in the
    /// list approves its current key, which is how a changed key is accepted.
    /// </summary>
    private static async Task<int> AddRepositoryAsync(CliServices s, string url, CancellationToken ct)
    {
        s.Out.Progress($"Reading {url}…");
        var probe = await s.Repositories.ProbeAsync(url, ct);
        if (!probe.Ok)
            return s.Out.Fail($"{url} is not a repository that can be added: [{probe.Reason}] {probe.Message}", details: new { reason = probe.Reason });
        var m = probe.Manifest!;
        var fingerprint = probe.Fingerprint!;
        var lines = new List<string>
        {
            $"{m.Repository.Name}  ({m.Repository.Id})",
            $"    from        {probe.Domain}",
            $"    maintainer  {m.Repository.Maintainer}",
        };
        if (!string.IsNullOrWhiteSpace(m.Repository.Description))
            lines.Add($"    {m.Repository.Description}");
        lines.Add($"    apps        {m.Providers.Count}");
        lines.Add($"    key         {RepositoryVerifier.FormatFingerprint(fingerprint)}");
        lines.Add("A valid signature means these apps come from this publisher, not that Apps2Samsung checked them.");

        var sources = UserSources.Parse(s.Desktop.UserSourcesJson);
        var existing = sources.FirstOrDefault(src => string.Equals(src.Url.Trim(), url, StringComparison.OrdinalIgnoreCase)
            || (src.RepositoryId is not null && string.Equals(src.RepositoryId, m.Repository.Id, StringComparison.Ordinal)));
        if (existing is not null && string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            return s.Out.Result(new { added = ToEntry(existing), unchanged = true }, new[] { $"{m.Repository.Name} is already in the list with this key." });
        if (existing?.Fingerprint is { } old)
            lines.Insert(0, $"The publisher's key changed: it was {RepositoryVerifier.FormatFingerprint(old)}. Approve the new one only if the publisher announced the change.");

        bool approved;
        if (!string.IsNullOrWhiteSpace(s.Options.Fingerprint))
        {
            approved = RepositoryVerifier.NormalizeFingerprint(s.Options.Fingerprint) == fingerprint;
            if (!approved)
                return s.Out.Fail($"the repository's key is {RepositoryVerifier.FormatFingerprint(fingerprint)}, not the --fingerprint given; nothing was added", Output.Stopped);
        }
        else if (s.Options.Yes)
            approved = true;
        else if (s.Out.Rich && s.Out.Terminal is { } con)
        {
            s.Out.Idle();
            foreach (var line in lines)
                con.MarkupLine(Markup.Escape(line));
            approved = await new ConfirmationPrompt(existing is null ? "Add this repository and trust this key?" : "Trust the new key?") { DefaultValue = false }.ShowAsync(con, ct);
        }
        else
        {
            return s.Out.Fail("approving a repository's key needs a yes: run this on a terminal, or pass --fingerprint <the key above> (or --yes)",
                Output.Stopped, new { repository = m.Repository, fingerprint, domain = probe.Domain });
        }
        if (!approved)
            return s.Out.Fail("not added", Output.Stopped);

        var source = probe.Approve(url, s.Options.Name);
        if (existing is not null)
            sources[sources.IndexOf(existing)] = source;
        else
            sources.Add(source);
        s.Desktop.UserSourcesJson = UserSources.Serialize(sources);
        var entry = ToEntry(source);
        var done = new List<string>(s.Out.Rich ? Array.Empty<string>() : lines)
        {
            existing is null
                ? $"Added {entry.Name}. `catalog` lists its apps from now on, labelled with its name."
                : $"Approved the new key for {entry.Name}.",
        };
        return s.Out.Result(new { added = entry, fingerprint, keyChanged = existing is not null }, done);
    }

    public static Entry ToEntry(UserSource source) => new(
        UserSources.DisplayName(source),
        source.Url.Trim(),
        UserSources.Classify(source.Url) switch
        {
            UserSourceKind.GitHubReleases => "github repository",
            UserSourceKind.GitHubReleaseTag => "github release",
            UserSourceKind.DirectFile => "file link",
            UserSourceKind.Feed => "feed",
            UserSourceKind.SignedRepository => UserSources.IsApprovedRepository(source) ? "signed repository" : "signed repository, key not approved",
            _ => "unreadable",
        },
        AppCategories.Normalize(source.Category),
        source.Partner,
        UserSources.ApiUrl(source.Url) ?? string.Empty,
        source.RepositoryId,
        source.Fingerprint);
}
