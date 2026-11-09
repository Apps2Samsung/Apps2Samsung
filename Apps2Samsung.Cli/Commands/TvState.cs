using Apps2Samsung.Design;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// A found TV as the CLI reports it, shared by <c>scan</c> and <c>tv</c>. The verdict is the apps'
/// TV card's (Core <see cref="TvCardStates"/>, the Developer Mode guide's Diagnose): a Host PC IP that
/// points elsewhere is "not ready" even with the debug port open. <paramref name="Route"/> says how
/// an install reaches the TV: "direct" (sdbd, port 26101) or "relay" (TV-Store's relay on the TV,
/// Host PC IP 127.0.0.1); <paramref name="RelayDown"/> when that relay does not answer.
/// </summary>
public sealed record TvState(
    string Ip,
    string? Name,
    string? Model,
    bool DeveloperMode,
    string? DeveloperIp,
    bool DebugPortOpen,
    bool Ready,
    string? NotReadyReason,
    string? Mac,
    string Route = "direct",
    bool RelayDown = false,
    string? Problem = null)
{
    public static TvState From(NetworkDevice d, string? localIp, IReadOnlyCollection<string>? localIps = null)
    {
        var ips = localIps ?? (localIp is null ? Array.Empty<string>() : new[] { localIp });
        var card = TvCardStates.ForDevice(d, ips, localIp, key => key);
        var problem = card.Problem;
        var ready = problem == DevModeProblem.None;
        var me = localIp is null ? "" : $" ({localIp})";
        string? reason = problem switch
        {
            DevModeProblem.None => null,
            DevModeProblem.DeveloperModeOff =>
                "Developer Mode is off. On the TV: Apps, type 1 2 3 4 5 on the remote, turn Developer mode on, enter this machine's IP"
                + me + ", then restart the TV.",
            DevModeProblem.IpMismatch =>
                $"Developer Mode points at {d.DeveloperIP}, not at this machine{me}. Change the Host PC IP on the TV, then restart it.",
            DevModeProblem.IpReversed =>
                $"Developer Mode points at {d.DeveloperIP}: that is this machine's IP{me} typed back to front. Correct the Host PC IP on the TV, then restart it.",
            DevModeProblem.RelayDown =>
                $"Host PC IP is 127.0.0.1, so installs go through TV-Store's relay on the TV (port {SdbEndpoint.RelayPort(d.IpAddress)}), and it does not answer. "
                + "On the TV, open TV-Store, go to Settings and turn on \"Connect from Apps2Samsung\" (TV-Store 0.3.14 or newer)."
                + (string.IsNullOrWhiteSpace(d.RelayError) ? "" : $" TV-Store says: {d.RelayError}"),
            _ => "Developer Mode looks right but the TV's install service is not running yet. Restart the TV (hold the power button until it shuts down, or unplug it for a minute).",
        };
        return new TvState(d.IpAddress, d.DeviceName, d.ModelName,
            string.Equals(d.DeveloperMode, "1", StringComparison.Ordinal), d.DeveloperIP,
            d.DebugPortOpen, ready, reason, d.MacAddress,
            card.ViaRelay ? "relay" : "direct", card.RelayDown,
            ready ? null : problem.ToString());
    }

    public string Line()
    {
        var who = string.Join(" ", new[] { Model, Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var status = Ready ? "ready" : "not ready";
        var route = Route == "relay" ? (RelayDown ? " (via TV-Store relay, relay off)" : " (via TV-Store relay)") : "";
        var dev = DeveloperMode
            ? $"developer mode on, host {DeveloperIp ?? "?"}{route}"
            : "developer mode off";
        return $"{Ip,-15} {status,-9} {dev}{(who.Length > 0 ? $"  [{who}]" : "")}";
    }
}

internal static class LocalIp
{
    /// <summary>
    /// This machine's LAN address, or null when it cannot be told: --local-ip, else the adapter picked
    /// in the desktop app's Settings, else the default route (which on a VPN is the tunnel's address).
    /// </summary>
    public static string? Of(CliServices s)
    {
        if (!string.IsNullOrWhiteSpace(s.Config.LocalIp))
            return s.Config.LocalIp;
        try { return s.Network.GetLocalIPAddress(); } catch { return null; }
    }

    /// <summary>Every address this machine answers on, plus <see cref="Of"/>: what a TV's Host PC IP is compared against.</summary>
    public static IReadOnlyCollection<string> All(CliServices s)
    {
        var list = new List<string>();
        try { list.AddRange(s.Network.GetRelevantLocalIPs().Select(ip => ip.ToString())); } catch { /* the one address below still counts */ }
        if (Of(s) is { } one && !list.Contains(one))
            list.Add(one);
        return list;
    }
}
