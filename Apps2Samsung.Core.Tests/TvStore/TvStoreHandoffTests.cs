using System.Net;
using System.Net.Sockets;
using System.Text;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Core.Tests.TvStore;

public sealed class TvStoreHandoffTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;

    public TvStoreHandoffTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public void Dispose() => _listener.Close();

    [Fact]
    public async Task A_running_TV_Store_is_ready_without_a_launch()
    {
        _listener.Start();
        var server = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var page = Encoding.UTF8.GetBytes("<html>upload</html>");
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(page);
            ctx.Response.Close();
        });
        var keys = new List<string>();

        Assert.True(await TvStoreHandoff.EnsureRunningAsync("127.0.0.1", keys.Add, port: _port));
        await server;
        // No launch was needed, so nothing was reported.
        Assert.Empty(keys);
    }
}
