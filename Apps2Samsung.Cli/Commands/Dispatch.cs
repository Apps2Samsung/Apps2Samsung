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
        _ => Task.FromResult(s.Out.Fail($"unknown command '{s.Options.Command}'\n{Help.Usage}", Output.Usage)),
    };

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
            return services.Out.Fail(ex.Message);
        }
    }
}
