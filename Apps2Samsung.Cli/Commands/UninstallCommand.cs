using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

public static class UninstallCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var id = s.Options.Arguments.FirstOrDefault();
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli uninstall <appId> --tv <ip>   (the app id as `installed` prints it)", Output.Usage);

        // The id goes to the TV as given, the same as the desktop and mobile Installed apps views do:
        // cutting it at the first dot broke ids such as org.tizen.netflix-app (#855).
        s.Out.Progress($"Removing {id} from {ip}…");
        var result = await s.Sdb.UninstallAsync(ip, id);
        // "failed[132]" = not installed: already gone, treat as success (as both heads do).
        var ok = result.ExitCode == 0 ||
                 (result.Output?.Contains("failed[132]", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!ok)
            return s.Out.Fail($"The TV did not remove {id}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() : result.Error)}");

        // The heads reload their list after an uninstall, so a refused app simply stays on screen. A
        // script has no screen: read the list again, because some TVs answer a refusal with no text at
        // all (store and preinstalled apps, #855).
        s.Out.Progress("Checking the TV's app list…");
        var apps = TizenInstalledApps.ParseOrThrow(await s.Sdb.AppsAsync(ip));
        if (apps.Any(a => string.Equals(a.TizenId, id, StringComparison.OrdinalIgnoreCase)))
            return s.Out.Fail($"The TV refused to remove {id}: store and preinstalled apps can only be removed from the TV's own Apps settings.");

        return s.Out.Result(new { ok = true, tv = ip, appId = id }, new[] { $"Removed {id}." });
    }
}
