using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Core.Tests.TvStore;

public sealed class TvStoreUploadTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly string _file = Path.Combine(Path.GetTempPath(), "a2s-upload-" + Guid.NewGuid().ToString("N") + ".wgt");
    private byte[] _received = Array.Empty<byte>();
    private string _path = "";

    public TvStoreUploadTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        File.WriteAllBytes(_file, Encoding.UTF8.GetBytes("PK fake package"));
    }

    public void Dispose()
    {
        _listener.Close();
        File.Delete(_file);
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Answers the next request with <paramref name="status"/> and <paramref name="json"/> (null: the body's real hash).</summary>
    private Task Serve(int status, Func<byte[], string> json) => Task.Run(async () =>
    {
        var ctx = await _listener.GetContextAsync();
        _path = ctx.Request.Url!.PathAndQuery;
        using var ms = new MemoryStream();
        await ctx.Request.InputStream.CopyToAsync(ms);
        _received = ms.ToArray();
        var reply = Encoding.UTF8.GetBytes(json(_received));
        ctx.Response.StatusCode = status;
        await ctx.Response.OutputStream.WriteAsync(reply);
        ctx.Response.Close();
    });

    [Fact]
    public async Task Sends_the_file_and_reads_what_waits_on_the_TV()
    {
        var server = Serve(200, body =>
            "{\"ok\":true,\"upload\":{\"id\":\"abc\",\"name\":\"x.wgt\",\"size\":" + body.Length + ",\"sha256\":\"" + Hex(body) +
            "\",\"ids\":{\"appId\":\"Pkg1234567.App\",\"packageId\":\"Pkg1234567\"}}}");
        var result = await TvStoreUpload.SendAsync("127.0.0.1", _file, port: _port);
        await server;
        Assert.Equal("abc", result.Id);
        Assert.Equal("Pkg1234567.App", result.AppId);
        Assert.Equal("Pkg1234567", result.PackageId);
        Assert.Equal(File.ReadAllBytes(_file), _received);
        Assert.StartsWith("/upload?name=a2s-upload-", _path);
    }

    [Fact]
    public async Task A_refusal_says_why()
    {
        var server = Serve(409, _ => """{"ok":false,"error":"the TV is installing the previous file; try again in a minute"}""");
        var ex = await Assert.ThrowsAsync<TvStoreUploadException>(() => TvStoreUpload.SendAsync("127.0.0.1", _file, port: _port));
        await server;
        Assert.Contains("installing the previous file", ex.Message);
    }

    [Fact]
    public async Task A_damaged_copy_on_the_TV_is_an_error()
    {
        var server = Serve(200, _ => """{"ok":true,"upload":{"id":"abc","name":"x.wgt","size":1,"sha256":"00","ids":{}}}""");
        var ex = await Assert.ThrowsAsync<TvStoreUploadException>(() => TvStoreUpload.SendAsync("127.0.0.1", _file, port: _port));
        await server;
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public async Task Nothing_listening_means_not_available()
    {
        _listener.Stop();
        Assert.False(await TvStoreUpload.IsAvailableAsync("127.0.0.1", port: _port));
    }
}
