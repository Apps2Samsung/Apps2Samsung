using System.Net;
using System.Net.Sockets;
using System.Text;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Core.Tests.TvStore;

// Pulling TV-Store's own log from GET /log.txt for a bug report.
public sealed class TvStoreLogTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-tvlog-").FullName;

    public TvStoreLogTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public void Dispose()
    {
        _listener.Close();
        Directory.Delete(_dir, recursive: true);
    }

    private Task Serve(int status, string body, Action<string>? path = null) => Task.Run(async () =>
    {
        var ctx = await _listener.GetContextAsync();
        path?.Invoke(ctx.Request.Url!.AbsolutePath);
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    });

    [Fact]
    public async Task SaveAsync_WritesTheLogWithAHeader_NextToTheDebugLogs()
    {
        _listener.Start();
        string? asked = null;
        var server = Serve(200, "--- this run ---\n10:00:00 TV-Store service on port 8077\n", p => asked = p);

        var path = await TvStoreLog.SaveAsync("127.0.0.1", _dir, port: _port);
        await server;

        Assert.Equal("/log.txt", asked);
        Assert.StartsWith(Path.Combine(_dir, "tvstore_127.0.0.1_"), path);
        var text = await File.ReadAllTextAsync(path);
        Assert.StartsWith("# TV-Store log from 127.0.0.1, fetched by Apps2Samsung ", text);
        Assert.Contains("TV-Store service on port 8077", text);
    }

    [Fact]
    public async Task FetchAsync_AnErrorStatus_IsUnavailable()
    {
        _listener.Start();
        var server = Serve(403, "only this TV may use TV-Store");

        var ex = await Assert.ThrowsAsync<TvStoreLogUnavailableException>(() => TvStoreLog.FetchAsync("127.0.0.1", port: _port));
        await server;

        Assert.Equal("HTTP 403", ex.Reason);
        Assert.Contains("Open TV-Store on the TV", ex.Message);
    }

    [Fact]
    public async Task FetchAsync_NobodyListening_IsUnavailable()
    {
        // The listener is never started: what a TV without TV-Store open looks like.
        var ex = await Assert.ThrowsAsync<TvStoreLogUnavailableException>(() => TvStoreLog.FetchAsync("127.0.0.1", port: _port));
        Assert.Equal("127.0.0.1", ex.TvIp);
    }

    [Fact]
    public void FileNameFor_SaysWhichTvAndWhen()
    {
        Assert.Equal("tvstore_192.168.1.20_2026-11-06_09-05-07.log",
            TvStoreLog.FileNameFor("192.168.1.20", new DateTime(2026, 11, 6, 9, 5, 7)));
    }
}
