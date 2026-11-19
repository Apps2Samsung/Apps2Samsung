// apps2samsung-cli: the terminal head of Apps2Samsung (#836). Scans for Samsung TVs, lists the app
// catalog and installs apps on a TV with Developer Mode, from a shell script or an AI agent, with
// the same Core pipeline the desktop and phone apps run. `apps2samsung-cli --help` for the verbs.

using System.Reflection;
using Apps2Samsung.Cli;
using Apps2Samsung.Cli.Commands;

var (options, parseError) = CliOptions.Parse(args);
if (parseError is not null)
{
    // One line and where to look (#1128): the whole usage after it pushed the error off the screen.
    // With --json a script still gets a document.
    if (args.Contains("--json"))
        Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = parseError }));
    else
    {
        Console.Error.WriteLine($"error: {parseError}");
        Console.Error.WriteLine("`apps2samsung-cli --help` lists the commands and options.");
    }
    return Output.Usage;
}
// `help` and `help <command>` read as `--help` and `<command> --help`.
if (options.Command == "help")
{
    options.Command = options.Arguments.FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
    options.Help = true;
}
if (options.ShowVersion)
{
    Console.Out.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
    return Output.Ok;
}
if (options.Help || options.Command.Length == 0)
{
    using var help = new Output(options);
    // No arguments on a terminal: the menu (#975). Piped, or with --json, the usage as before.
    if (options.Command.Length == 0 && !options.Help && help.Rich)
    {
        options.Command = "menu";
    }
    else
    {
        help.Help(Help.For(options.Command));
        return options.Command.Length == 0 && !options.Help ? Output.Usage : Output.Ok;
    }
}

// What `self-update` could not delete while the old copy was still running (Windows only).
if (OperatingSystem.IsWindows())
    Apps2Samsung.Update.UpdateFiles.RemoveLeftovers(AppContext.BaseDirectory);

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first ctrl+c asks the command to stop. A second one, while it has not (a call to the TV
    // that takes no cancellation), is left to end the process (#1128).
    if (cancel.IsCancellationRequested)
        return;
    e.Cancel = true;
    cancel.Cancel();
};

await using var services = new CliServices(options);
try
{
    return await Dispatch.RunAsync(services, cancel.Token);
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    // ctrl+c is how the menu is left, as its header says: that is not a failure.
    if (options.Command == "menu")
        return Output.Ok;
    return services.Out.Fail("cancelled", Output.Failed);
}
catch (Exception ex)
{
    return services.Out.Fail(Dispatch.Unexpected(ex, options));
}
