using Apps2Samsung.Sdb;
using Apps2Samsung.Services;

namespace Apps2Samsung.Cli.Commands;

public static class TvCommand
{
    public static async Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        var ip = s.Options.Arguments.FirstOrDefault() ?? s.Options.Tv;
        if (string.IsNullOrWhiteSpace(ip))
            return s.Out.Fail("usage: apps2samsung-cli tv <ip>", Output.Usage);

        s.Out.Progress($"Checking {ip}…");
        var device = await s.Network.ValidateManualTizenAddress(ip, ct);
        if (device is null)
            return s.Out.Fail($"No Samsung TV answers at {ip} (nothing on port 8001 or 26101). Is the TV on and on this network?");

        var localIp = LocalIp.Of(s);
        device = await TizenDeveloperInfo.ReadAsync(device, ct);
        var state = TvState.From(device, localIp);

        string? duid = null, tizen = null;
        if (device.DebugPortOpen)
        {
            s.Out.Progress("Reading the TV over the debug port…");
            try
            {
                var info = await TizenDeviceInfoService.GatherAsync(s.Sdb, ip, true, localIp);
                duid = string.IsNullOrWhiteSpace(info.Duid) ? null : info.Duid;
                tizen = string.IsNullOrWhiteSpace(info.TizenVersion) ? null : info.TizenVersion;
            }
            catch (Exception ex)
            {
                s.Out.Warn($"the debug port is open but the TV did not answer properly: {ex.Message}");
            }
        }

        var text = new List<string> { state.Line() };
        if (tizen is not null) text.Add($"Tizen {tizen}");
        if (duid is not null) text.Add($"DUID {duid}");
        if (state.NotReadyReason is not null) text.Add(state.NotReadyReason);
        return s.Out.Result(new { ok = true, localIp, tv = state, tizenVersion = tizen, duid }, text);
    }
}
