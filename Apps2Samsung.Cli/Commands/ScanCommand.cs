using Apps2Samsung.Services;

namespace Apps2Samsung.Cli.Commands;

public static class ScanCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        s.Out.Progress("Scanning the network for Samsung TVs…");
        var localIp = LocalIp.Of(s);
        var found = await TizenDeveloperInfo.ScanAsync(s.Network, ct);
        var tvs = found.Select(d => TvState.From(d, localIp)).OrderBy(t => t.Ip, StringComparer.Ordinal).ToList();

        var text = new List<string>();
        if (tvs.Count == 0)
        {
            text.Add("No Samsung TV found on this network.");
            if (s.Network.LastScanBlockedByOs)
                text.Add("Every probe was blocked before it left this machine: the OS is denying local network access to this program.");
            text.Add("A TV must be on, on the same network, and answer on port 8001; `tv <ip>` checks one address directly.");
        }
        foreach (var tv in tvs)
        {
            text.Add(tv.Line());
            if (tv.NotReadyReason is not null)
                text.Add($"                {tv.NotReadyReason}");
        }
        return s.Out.Result(new { ok = true, localIp, tvs, blockedByOs = s.Network.LastScanBlockedByOs }, text);
    }
}
