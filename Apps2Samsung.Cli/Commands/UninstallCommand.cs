namespace Apps2Samsung.Cli.Commands;

public static class UninstallCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var id = s.Options.Arguments.FirstOrDefault();
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli uninstall <packageId> --tv <ip>   (the package id is the part of the app id before the dot)", Output.Usage);

        // An app id ("AprZAARz4r.Jellyfin") is what `installed` prints; the TV removes by package id.
        var packageId = id.Contains('.') ? id[..id.IndexOf('.')] : id;
        s.Out.Progress($"Removing {packageId} from {ip}…");
        var result = await s.Sdb.UninstallAsync(ip, packageId);
        var ok = result.ExitCode == 0 && !result.Output.Contains("fail", StringComparison.OrdinalIgnoreCase);
        return ok
            ? s.Out.Result(new { ok = true, tv = ip, packageId }, new[] { $"Removed {packageId}." })
            : s.Out.Fail($"The TV did not remove {packageId}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error)}");
    }
}
