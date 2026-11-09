namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// <c>logs</c> (#975): where the session logs are and which ones there are, newest first;
/// <c>logs show</c> prints the newest one, the run before this command, as it is, for a bug report
/// (<c>apps2samsung-cli logs show &gt; a2s.log</c>).
/// </summary>
public static class LogsCommand
{
    public static Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var logs = CliLog.List();
        var verb = s.Options.Arguments.FirstOrDefault();

        if (string.Equals(verb, "show", StringComparison.OrdinalIgnoreCase))
        {
            var newest = logs.FirstOrDefault();
            if (newest is null)
                return Task.FromResult(s.Out.Fail($"No logs yet in {CliLog.Folder}: every other command writes one."));
            string text;
            try { text = File.ReadAllText(newest.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(s.Out.Fail($"Could not read {newest.FullName}: {ex.Message}"));
            }
            // As it is, never as a card: the point is a file to attach.
            Output.StdOut.Write(text);
            return Task.FromResult(Output.Ok);
        }
        if (verb is not null)
            return Task.FromResult(s.Out.Fail($"unknown logs command '{verb}'\n{Help.For("logs")}", Output.Usage));

        var lines = new List<string> { $"Logs in {CliLog.Folder}, newest first (the last {CliLog.Keep} runs are kept):" };
        lines.AddRange(logs.Take(10).Select(f => $"  {f.Name}  {Kb(f.Length)}"));
        if (logs.Count == 0)
            lines.Add("  none yet: every other command writes one.");
        else
        {
            lines.Add($"The newest: {logs[0].FullName}");
            lines.Add("Print it with: apps2samsung-cli logs show");
            lines.Add(CliLog.ShareHint(logs[0].FullName));
        }
        return Task.FromResult(s.Out.Result(
            new
            {
                ok = true,
                folder = CliLog.Folder,
                logs = logs.Select(f => new { name = f.Name, path = f.FullName, size = f.Length, modified = f.LastWriteTimeUtc }),
            },
            lines));
    }

    private static string Kb(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";
}
