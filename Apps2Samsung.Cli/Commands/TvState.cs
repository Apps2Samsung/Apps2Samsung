using Apps2Samsung.Models;
using Apps2Samsung.Services;

namespace Apps2Samsung.Cli.Commands;

/// <summary>A found TV as the CLI reports it, shared by <c>scan</c> and <c>tv</c>.</summary>
public sealed record TvState(
    string Ip,
    string? Name,
    string? Model,
    bool DeveloperMode,
    string? DeveloperIp,
    bool DebugPortOpen,
    bool Ready,
    string? NotReadyReason,
    string? Mac)
{
    public static TvState From(NetworkDevice d, string? localIp)
    {
        var ready = d.DebugPortOpen;
        string? reason = null;
        if (!ready)
        {
            reason = TizenDeviceReadiness.WhyNotReady(d, localIp) switch
            {
                TizenDeviceReadiness.NotReadyReason.DeveloperModeOff =>
                    "Developer Mode is off. On the TV: Apps, type 1 2 3 4 5 on the remote, turn Developer mode on, enter this machine's IP"
                    + (localIp is null ? "" : $" ({localIp})") + ", then restart the TV.",
                TizenDeviceReadiness.NotReadyReason.DeveloperIpMismatch =>
                    $"Developer Mode points at {d.DeveloperIP}, not at this machine" + (localIp is null ? "" : $" ({localIp})") + ". Change the Host PC IP on the TV, then restart it.",
                _ => "Developer Mode looks right but the TV's install service is not running yet. Restart the TV (hold the power button until it shuts down, or unplug it for a minute).",
            };
        }
        return new TvState(d.IpAddress, d.DeviceName, d.ModelName,
            string.Equals(d.DeveloperMode, "1", StringComparison.Ordinal), d.DeveloperIP,
            d.DebugPortOpen, ready, reason, d.MacAddress);
    }

    public string Line()
    {
        var who = string.Join(" ", new[] { Model, Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var status = Ready ? "ready" : "not ready";
        var dev = DeveloperMode ? $"developer mode on, host {DeveloperIp ?? "?"}" : "developer mode off";
        return $"{Ip,-15} {status,-9} {dev}{(who.Length > 0 ? $"  [{who}]" : "")}";
    }
}

internal static class LocalIp
{
    /// <summary>This machine's LAN address, or null when it cannot be told.</summary>
    public static string? Of(CliServices s)
    {
        try { return s.Network.GetLocalIPAddress(); } catch { return null; }
    }
}
