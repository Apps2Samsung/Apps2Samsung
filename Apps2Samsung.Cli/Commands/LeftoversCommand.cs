using Apps2Samsung.Agent;
using Apps2Samsung.Certificate;
using Apps2Samsung.Install;
using Apps2Samsung.Localization;
using Apps2Samsung.Models;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// <c>leftovers --tv &lt;ip&gt;</c>: the TV's install staging folder, the toolbox's "Install leftovers"
/// card (#681, #975). Every package that gets installed is first copied there and left behind, and
/// the TV's own clean-up does nothing on retail firmware, so the Apps2Samsung Debug agent, a small
/// app on the TV, lists the folder and deletes files on request. The agent is installed like any
/// package when the TV lacks it (or runs an older one), through the CLI's own certificate and
/// install steps, then attached over the developer connection for the length of the command.
/// </summary>
public static class LeftoversCommand
{
    public sealed record Row(string Name, string Path, long Size, string? Modified, bool IsPackage);

    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Tv ?? s.Options.Arguments.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli leftovers --tv <ip> [--clear | --delete <file>]   (`leftovers --help` for the details)", Output.Usage);
        if (s.Options.Clear && s.Options.DeleteFile is not null)
            return s.Out.Fail("--clear and --delete are one or the other: --clear takes every package, --delete one file by name", Output.Usage);

        DebugAgentClient agent;
        try
        {
            agent = await AttachAsync(s, ip, ct);
        }
        catch (DebugAgentInstallException ex)
        {
            return s.Out.Fail($"{s.Strings.GetString(ex.Key)} ({ex.Message})");
        }
        catch (TvUnreachableException ex)
        {
            // The TV's sdbd refused this machine, or nothing listens: the same answer `tvstore` gives (#889).
            return s.Out.Fail(
                $"{ip} did not list its apps ({ex.Message}), so the staging folder can't be read. On the TV, Developer Mode must be on and " +
                "Host PC IP must be this machine's current address (or 127.0.0.1 with TV-Store's relay turned on); restart the TV after changing either.");
        }

        await using (agent)
        {
            if (!agent.SupportsStaging)
            {
                return s.Out.Fail(
                    $"The debug agent on the TV is v{agent.AgentVersion}; reading the staging folder needs v{DebugAgentClient.StagingSince} or later, " +
                    "and this build could not replace it. Run the command again; if the same answer comes back, remove the Apps2Samsung Debug agent on the TV and retry.");
            }

            // ---- One file by name ----
            if (s.Options.DeleteFile is { } name)
            {
                var before = await ListAsync(s, agent, ct);
                var file = before.Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                           ?? before.Files.FirstOrDefault(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase));
                if (file is null)
                {
                    return s.Out.Fail(before.Files.Count == 0
                        ? $"The staging folder on {ip} is empty; nothing called {name} to delete."
                        : $"No file called {name} in the staging folder on {ip}. There: {string.Join(", ", before.Files.Select(f => f.Name))}");
                }
                if (!agent.SupportsStagedDelete)
                {
                    return s.Out.Fail(
                        $"The debug agent on the TV is v{agent.AgentVersion}; deleting one file needs v{DebugAgentClient.StagedDeleteSince} or later. " +
                        "Run the command again so the agent gets replaced; --clear takes every package on any agent that lists the folder.");
                }
                s.Out.Progress(string.Format(s.Strings.GetString("lblToolboxStagingDeleting"), file.Name));
                await agent.DeleteStagedFileAsync(file, ct);
                var line = string.Format(s.Strings.GetString("lblToolboxStagingDeleted"), file.Name, InstalledApp.FormatSize(file.Size));
                return s.Out.Result(
                    new { ok = true, tv = ip, deleted = new[] { ToRow(file) }, freedBytes = file.Size, left = (await ListAsync(s, agent, ct)).Files.Count },
                    new[] { line },
                    rich: Views.Done(new[] { line }, s.Out.Unicode));
            }

            // ---- Every package ----
            if (s.Options.Clear)
            {
                var before = await ListAsync(s, agent, ct);
                if (!before.Packages.Any())
                {
                    var none = s.Strings.GetString("lblToolboxStagingEmpty");
                    var kept = before.Files.Count - before.Packages.Count();
                    var lines = kept == 0
                        ? new[] { none }
                        : new[] { none, $"{kept} other file{(kept == 1 ? "" : "s")} there; --delete <file> takes one by name." };
                    return s.Out.Result(new { ok = true, tv = ip, deleted = Array.Empty<Row>(), failed = Array.Empty<string>(), freedBytes = 0L, left = before.Files.Count },
                        lines, rich: Views.Done(lines, s.Out.Unicode));
                }
                s.Out.Progress(s.Strings.GetString("lblToolboxStagingClearing"));
                var result = await agent.ClearStagingAsync(ct);
                var summary = result.Failed.Count == 0
                    ? string.Format(s.Strings.GetString("lblToolboxStagingCleared"), result.Deleted.Count, InstalledApp.FormatSize(result.FreedBytes))
                    : string.Format(s.Strings.GetString("lblToolboxStagingPartial"), result.Deleted.Count, result.Failed.Count, string.Join("; ", result.Failed));
                foreach (var f in result.Failed)
                    s.Out.Warn(f);
                return s.Out.Result(
                    new { ok = result.Failed.Count == 0, tv = ip, deleted = result.Deleted.Select(ToRow), failed = result.Failed, freedBytes = result.FreedBytes },
                    new[] { summary },
                    result.Failed.Count == 0 ? Output.Ok : Output.Failed,
                    Views.Done(new[] { summary }, s.Out.Unicode));
            }

            // ---- The listing ----
            var listing = await ListAsync(s, agent, ct);
            var rows = listing.Files.Select(ToRow).ToList();
            var text = new List<string>();
            foreach (var r in rows)
                text.Add($"{r.Name}  {InstalledApp.FormatSize(r.Size)}  {r.Modified ?? ""}  {(r.IsPackage ? "package" : s.Strings.GetString("lblToolboxStagingKept"))}");
            text.Add(Summary(s, listing));
            foreach (var e in listing.Errors)
                s.Out.Warn(e);
            return s.Out.Result(
                new { ok = true, tv = ip, agentVersion = agent.AgentVersion, count = rows.Count, packages = listing.Packages.Count(), packageBytes = listing.PackageBytes, files = rows, errors = listing.Errors },
                text,
                rich: Views.Leftovers(rows, Summary(s, listing), ip, s.Out.Width, s.Out.Unicode));
        }
    }

    /// <summary>The folder as the agent lists it, for the command and for the menu.</summary>
    public static async Task<DebugAgentStaging> ListAsync(CliServices s, DebugAgentClient agent, CancellationToken ct)
    {
        s.Out.Progress(s.Strings.GetString("lblToolboxStagingListing"));
        return await agent.ListStagingAsync(ct);
    }

    /// <summary>
    /// The agent on the TV, attached: installed first when the TV lacks it, replaced when it is older
    /// than the one this build embeds. The install is the CLI's own: the certificate as for any package
    /// (a Samsung sign-in in the browser when none covers the TV yet), then Core's installer.
    /// </summary>
    /// <exception cref="TvUnreachableException">The TV sent no app listing: no listing is not "no agent" (#889).</exception>
    public static async Task<DebugAgentClient> AttachAsync(CliServices s, string ip, CancellationToken ct)
    {
        // The agent's own check reads an empty listing as "not installed" and would go on to install
        // it, certificate and all, against a TV that refused the connection. Ask first, as `tvstore` does.
        s.Out.Progress($"Looking for the debug agent on {ip}…");
        try
        {
            Sdb.TizenInstalledApps.ParseOrThrow(await s.Sdb.AppsAsync(ip));
        }
        catch (InvalidOperationException ex)
        {
            throw new TvUnreachableException(ex.Message, ex);
        }

        var progress = new Progress<string>(key => s.Out.Progress(s.Strings.GetString(key)));
        Func<string, Action<string>, Task<bool>> installWgt = async (wgtPath, report) =>
        {
            SigningProfile profile;
            if (Certificates.UseTizen(s))
            {
                (profile, _) = await Certificates.EnsureTizenAsync(s, Certificates.TizenLevel(s));
            }
            else
            {
                var level = CertificatePreference.ResolveLevel(Certificates.Preference(s), requirePartner: false);
                (profile, _, _) = await Certificates.EnsureAsync(s, ip, level, ct);
            }
            await s.Installer.InstallAsync(ip, wgtPath, profile,
                progress: line => report(ProgressText.Localize(line, s.Strings.GetString)),
                ct: ct,
                confirmRemove: () => Task.FromResult(true));
            return true;
        };
        return await DebugAgentClient.AttachCurrentAsync(s.Sdb, ip, installWgt, message => s.Out.Progress(message), progress, ct);
    }

    public static string Summary(CliServices s, DebugAgentStaging listing)
    {
        var packages = listing.Packages.Count();
        return packages == 0
            ? s.Strings.GetString("lblToolboxStagingEmpty")
            : string.Format(s.Strings.GetString("lblToolboxStagingSummary"), packages, InstalledApp.FormatSize(listing.PackageBytes));
    }

    public static Row ToRow(DebugAgentStagedFile f) =>
        new(f.Name, f.Path, f.Size, f.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), f.IsPackage);

    /// <summary>The TV sent no app listing over the developer connection, so nothing about the agent can be told.</summary>
    public sealed class TvUnreachableException : Exception
    {
        public TvUnreachableException(string message, Exception inner) : base(message, inner) { }
    }
}
