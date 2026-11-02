using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Apps2Samsung.Diagnostics;

namespace Apps2Samsung.Tests.Diagnostics;

/// <summary>
/// Attaching to an inspector that lacks a domain the console only wants for extras (#954): a 2017
/// (Tizen 3) set has no Log domain, and the attach must not fail over it.
/// </summary>
public class DevToolsConsoleAttachTests
{
    [Fact]
    public async Task AttachesWhenTheInspectorHasNoLogDomain()
    {
        var methods = new List<string>();
        var (url, _) = await StartInspectorAsync(methods, missing: "Log.enable");

        await using var console = new DevToolsConsole();
        await console.ConnectAsync(url);

        Assert.Contains("Runtime.enable", methods);
        Assert.Contains("Log.enable", methods);
    }

    [Fact]
    public async Task StillFailsWhenRuntimeIsMissing()
    {
        var (url, _) = await StartInspectorAsync(new List<string>(), missing: "Runtime.enable");

        await using var console = new DevToolsConsole();
        await Assert.ThrowsAsync<InvalidOperationException>(() => console.ConnectAsync(url));
    }

    // A tiny inspector: answers every command with an empty result except the one it lacks. Plain
    // TCP with the WebSocket handshake done by hand, so the test needs no HTTP server.
    private static Task<(Uri Url, Task Server)> StartInspectorAsync(List<string> methods, string missing)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();

                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one) == 0)
                        return;
                    head.Append((char)one[0]);
                }

                var key = Regex.Match(head.ToString(), @"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));

                using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
                var buffer = new byte[8192];
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close)
                        break;

                    var request = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count))!;
                    var method = request["method"]!.GetValue<string>();
                    lock (methods)
                        methods.Add(method);

                    var reply = new JsonObject { ["id"] = request["id"]!.GetValue<int>() };
                    if (method == missing)
                        reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"'{method}' wasn't found" };
                    else
                        reply["result"] = new JsonObject();
                    await socket.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch
            {
                // The test closing its side ends the loop; nothing to report.
            }
            finally
            {
                listener.Stop();
            }
        });

        return Task.FromResult((new Uri($"ws://127.0.0.1:{port}/"), server));
    }
}
