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
        var result = await s.Sdb.LaunchAsync(ip, appId);
        if (result.ExitCode != 0)
        {
            // The platform launcher knows ids Smart Hub does not (system apps, services).
            var second = await s.Sdb.ExecuteAsync(ip, appId);
            if (second.ExitCode == 0)
                result = second;
        }
        return result.ExitCode == 0
            ? s.Out.Result(new { ok = true, tv = ip, appId, reply = result.Output.Trim() }, new[] { $"Launched {appId}." })
            : s.Out.Fail($"The TV did not launch {appId}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error)}");
    }
}
