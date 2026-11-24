using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

public static class UninstallCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var id = s.Options.Arguments.FirstOrDefault();
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli uninstall <appId> --tv <ip> [--yes]   (the app id as `installed` prints it)", Output.Usage);

        // An app the TV lists as one of its own (#1175) is asked about first, as the desktop and
        // mobile apps do: the TV may keep it, and one it does remove cannot be installed again from
        // here. A list that cannot be read is no reason to stop: the uninstall says what the TV does.
        InstalledApp? listed = null;
        try
        {
            s.Out.Progress("Reading the TV's app list…");
            listed = TizenInstalledApps.ParseOrThrow(await s.Sdb.AppsAsync(ip))
                .FirstOrDefault(a => string.Equals(a.TizenId, id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            s.Out.Warn($"The TV's app list could not be read first: {ex.Message}");
        }

        var builtIn = listed is { IsRemovable: false };
        var name = listed?.DisplayName ?? id;
        if (builtIn && !s.Options.Yes)
            return s.Out.Result(new { ok = false, tv = ip, appId = id, removed = false, builtIn = true },
                BuiltInQuestion(s.Strings.GetString, name, id).Append("Nothing was removed: add --yes to ask the TV to remove it."), Output.Stopped);

        // The id goes to the TV as given, the same as the desktop and mobile Installed apps views do:
        // cutting it at the first dot broke ids such as org.tizen.netflix-app (#855).
        s.Out.Progress($"Removing {id} from {ip}…");
        // The outcome is read off the TV's app list afterwards: some TVs answer a refusal with no
        // text at all (store and preinstalled apps, #855), and a script has no screen to see it on.
        var verdict = await AppUninstall.RunAsync(s.Sdb, ip, id);

        switch (verdict.Outcome)
        {
            case UninstallOutcome.Kept when builtIn:
                return s.Out.Fail(AppUninstall.KeptBuiltInMessage(name, verdict.Reply, s.Strings.GetString));
            case UninstallOutcome.Kept when verdict.Verified:
                return s.Out.Fail($"The TV refused to remove {id}{(verdict.Reply is null ? "" : $" (it answered: {verdict.Reply})")}: " +
                    "it is still in the TV's app list.");
            case UninstallOutcome.Kept:
                return s.Out.Fail($"The TV did not remove {id}: {verdict.Reply}");
            // Still a success for a script that only wants the app gone, but not "Removed": a mistyped id,
            // or a catalog name where the app id belongs, used to read as a removal (#1128).
            case UninstallOutcome.NotInstalled:
                return s.Out.Result(new { ok = true, tv = ip, appId = id, removed = false },
                    new[] { $"{id} is not installed on {ip}, nothing was removed. `apps2samsung-cli installed --tv {ip}` prints the app ids." });
            default:
                return s.Out.Result(new { ok = true, tv = ip, appId = id, removed = true }, new[] { $"Removed {id}." });
        }
    }

    /// <summary>
    /// The warning before removing an app the TV lists as one of its own, in the words every head
    /// uses (en.json), one line of text per element.
    /// </summary>
    public static IEnumerable<string> BuiltInQuestion(Func<string, string> localize, string appName, string appId) =>
        string.Format(localize(AppUninstall.ConfirmBuiltInKey), appName, appId)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
