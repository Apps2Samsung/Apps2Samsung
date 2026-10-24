// apps2samsung-cli: the terminal head of Apps2Samsung (#836). Scans for Samsung TVs, lists the app
// catalog and installs apps on a TV with Developer Mode, from a shell script or an AI agent, with
// the same Core pipeline the desktop and phone apps run. `apps2samsung-cli --help` for the verbs.

using System.Reflection;
using Apps2Samsung.Cli;
using Apps2Samsung.Cli.Commands;

var (options, parseError) = CliOptions.Parse(args);
if (parseError is not null)
{
    Console.Error.WriteLine($"error: {parseError}");
    Console.Error.WriteLine(Help.Usage);
    return Output.Usage;
}
if (options.ShowVersion)
{
    Console.Out.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
    return Output.Ok;
}
if (options.Help || options.Command.Length == 0)
{
    Console.Out.WriteLine(Help.For(options.Command));
    return options.Command.Length == 0 && !options.Help ? Output.Usage : Output.Ok;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

await using var services = new CliServices(options);
try
{
    return options.Command switch
    {
        "scan" => await ScanCommand.RunAsync(services, cancel.Token),
        "tv" => await TvCommand.RunAsync(services, cancel.Token),
        "catalog" or "search" or "apps" => await CatalogCommand.RunAsync(services, cancel.Token),
        "installed" => await InstalledCommand.RunAsync(services, cancel.Token),
        "install" => await InstallCommand.RunAsync(services, cancel.Token),
        "uninstall" or "remove" => await UninstallCommand.RunAsync(services, cancel.Token),
        "launch" or "open" => await LaunchCommand.RunAsync(services, cancel.Token),
        "cert" or "certificate" => await CertCommand.RunAsync(services, cancel.Token),
        _ => services.Out.Fail($"unknown command '{options.Command}'\n{Help.Usage}", Output.Usage),
    };
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    return services.Out.Fail("cancelled", Output.Failed);
}
catch (Exception ex)
{
    return services.Out.Fail(ex.Message);
}
