namespace Apps2Samsung.Cli.Commands;

public static class LaunchCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var appId = s.Options.Arguments.FirstOrDefault();
        var ip = s.Options.Tv;
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli launch <appId> --tv <ip>", Output.Usage);

        s.Out.Progress($"Launching {appId} on {ip}…");
        // LaunchAsync only, as the apps' Launch button: the `0 execute` verb answers exit 0 whatever the
        // TV replies, so it cannot tell a launch from a refusal.
        var result = await s.Sdb.LaunchAsync(ip, appId);
        return result.ExitCode == 0
            ? s.Out.Result(new { ok = true, tv = ip, appId, reply = result.Output.Trim() }, new[] { $"Launched {appId}." })
            : s.Out.Fail($"The TV did not launch {appId}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error)}");
    }
}
