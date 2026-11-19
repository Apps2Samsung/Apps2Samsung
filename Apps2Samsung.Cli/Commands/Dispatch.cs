namespace Apps2Samsung.Cli.Commands;

/// <summary>The verb table: one place that turns a parsed command line into the command that runs it, for Program and for the menu.</summary>
public static class Dispatch
{
    public static Task<int> RunAsync(CliServices s, CancellationToken ct) => s.Options.Command switch
    {
        "scan" => ScanCommand.RunAsync(s, ct),
        "tv" => TvCommand.RunAsync(s, ct),
        "catalog" or "search" or "apps" => CatalogCommand.RunAsync(s, ct),
        "installed" => InstalledCommand.RunAsync(s, ct),
        "install" => InstallCommand.RunAsync(s, ct),
        "update" or "upgrade" => UpdateCommand.RunAsync(s, ct),
        "sign" => SignCommand.RunAsync(s, ct),
        "uninstall" or "remove" => UninstallCommand.RunAsync(s, ct),
        "launch" or "open" => LaunchCommand.RunAsync(s, ct),
        "cert" or "certificate" => CertCommand.RunAsync(s, ct),
        "tvstore" or "tv-store" => TvStoreCommand.RunAsync(s, ct),
        "sources" or "source" => SourcesCommand.RunAsync(s, ct),
        "menu" => MenuCommand.RunAsync(s, ct),
        "leftovers" or "staging" => LeftoversCommand.RunAsync(s, ct),
        "logs" or "log" => LogsCommand.RunAsync(s, ct),
        "self-update" or "selfupdate" => SelfUpdateCommand.RunAsync(s, ct),
        _ => Task.FromResult(s.Out.Fail(Unknown(s.Options.Command), Output.Usage)),
    };

    /// <summary>Every verb and alias the table above takes, for the "did you mean" of a mistyped one.</summary>
    public static readonly string[] Verbs =
    {
        "scan", "tv", "catalog", "search", "apps", "installed", "install", "update", "upgrade", "sign",
        "uninstall", "remove", "launch", "open", "cert", "certificate", "tvstore", "tv-store", "sources",
        "source", "menu", "leftovers", "staging", "logs", "log", "self-update", "selfupdate", "help",
    };

    /// <summary>One line for a verb that does not exist (#1128): the closest real one when there is
    /// one, and where the list is. The whole usage after it pushed the error off the screen.</summary>
    public static string Unknown(string command)
    {
        var closest = Verbs
            .Select(v => (Verb: v, Distance: Distance(command, v)))
            // one slip in a short verb, two (a swapped pair) from four letters on
            .Where(c => c.Distance <= Math.Min(2, Math.Max(1, c.Verb.Length / 2)))
            .OrderBy(c => c.Distance)
            .Select(c => c.Verb)
            .FirstOrDefault();
        return $"unknown command '{command}'.{(closest is null ? "" : $" Did you mean '{closest}'?")} `apps2samsung-cli --help` lists the commands.";
    }

    /// <summary>Levenshtein distance: how many single-character edits turn one word into the other.</summary>
    private static int Distance(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>
    /// What to say for an exception nothing expected (#1128): the message, and in text mode where
    /// the rest is. The type and the stack go to the session log, which used to hold the same one line.
    /// </summary>
    public static string Unexpected(Exception ex, CliOptions options)
    {
        System.Diagnostics.Trace.WriteLine($"[cli] unexpected: {ex}");
        return options.Json ? ex.Message : $"{ex.Message}\n  The session log has the details: `apps2samsung-cli logs show`";
    }

    /// <summary>
    /// Runs one command line as if it had been typed: its own services and output, so what the menu
    /// does is exactly what the line it shows would do. Exceptions become the exit code, as in Program.
    /// <paramref name="sdb"/> is the caller's engine, so the line talks to the TV over the caller's
    /// connection instead of opening a second one (see <see cref="CliServices"/>).
    /// </summary>
    public static async Task<int> RunLineAsync(IReadOnlyList<string> args, CancellationToken ct, Apps2Samsung.Sdb.InProcessSdbEngine? sdb = null)
    {
        var (options, parseError) = CliOptions.Parse(args.ToArray());
        if (parseError is not null)
        {
            Console.Error.WriteLine($"error: {parseError}");
            return Output.Usage;
        }
        await using var services = new CliServices(options, sdb);
        try
        {
            return await RunAsync(services, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return services.Out.Fail("cancelled", Output.Failed);
        }
        catch (Exception ex)
        {
            return services.Out.Fail(Unexpected(ex, options));
        }
    }
}
