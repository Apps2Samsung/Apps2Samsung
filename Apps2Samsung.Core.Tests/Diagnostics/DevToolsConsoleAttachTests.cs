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
        Assert.Contains("Log=no", console.Capabilities);
    }

    // What an inspector answers for a promise it does not await, and for one it does.
    private static JsonObject ByValue(JsonNode? value) => new() { ["result"] = new JsonObject { ["type"] = "object", ["value"] = value } };

    [Fact(Timeout = 20000)]
    public async Task AnInspectorThatIgnoresAwaitPromiseIsPolledForTheResult()
    {
        var expressions = new List<string>();
        var polls = 0;
        var (url, _) = await StartInspectorAsync(new List<string>(), (method, parameters) =>
        {
            if (method != "Runtime.evaluate")
                return (new JsonObject(), null);

            var expression = parameters?["expression"]?.GetValue<string>() ?? "";
            lock (expressions)
                expressions.Add(expression);

            // The probe: this inspector hands the promise back as an empty object.
            if (expression == "Promise.resolve(1)")
                return (ByValue(new JsonObject()), null);
            // The polls: not settled on the first, settled on the second.
            if (expression.StartsWith("window[", StringComparison.Ordinal))
                return (ByValue(++polls < 2
                    ? new JsonObject { ["done"] = false }
                    : new JsonObject { ["done"] = true, ["ok"] = true, ["t"] = "{\"ok\":true,\"certReady\":false}" }), null);
            return (new JsonObject(), null);
        });

        await using var console = new DevToolsConsole();
        await console.ConnectAsync(url);
        var value = await console.EvaluateValueAsync("A2SStore.status()");

        Assert.True(value?["ok"]?.GetValue<bool>());
        Assert.Equal(2, polls);
        // A copy under the lock: the fake inspector may still log a late poll from its own thread
        // while the asserts enumerate.
        List<string> seen;
        lock (expressions)
            seen = expressions.ToList();
        // The expression ran once, inside the wrapper, never on its own.
        Assert.Single(seen, e => e.Contains("A2SStore.status()", StringComparison.Ordinal));
        Assert.DoesNotContain("A2SStore.status()", seen);
    }

    [Fact(Timeout = 20000)]
    public async Task APollingEvaluationThatFailsInThePageThrowsItsMessage()
    {
        var (url, _) = await StartInspectorAsync(new List<string>(), (method, parameters) =>
        {
            var expression = parameters?["expression"]?.GetValue<string>() ?? "";
            if (method == "Runtime.evaluate" && expression == "Promise.resolve(1)")
                return (ByValue(new JsonObject()), null);
            if (method == "Runtime.evaluate" && expression.StartsWith("window[", StringComparison.Ordinal))
                return (ByValue(new JsonObject { ["done"] = true, ["ok"] = false, ["e"] = "A2SStore is not defined" }), null);
            return (new JsonObject(), null);
        });

        await using var console = new DevToolsConsole();
        await console.ConnectAsync(url);

        var failure = await Assert.ThrowsAsync<DevToolsEvaluationException>(() => console.EvaluateValueAsync("A2SStore.status()"));
        Assert.Contains("A2SStore is not defined", failure.Message);
    }

    [Fact(Timeout = 20000)]
    public async Task AnInspectorThatAwaitsIsEvaluatedInOneCall()
    {
        var expressions = new List<string>();
        var (url, _) = await StartInspectorAsync(new List<string>(), (method, parameters) =>
        {
            var expression = parameters?["expression"]?.GetValue<string>() ?? "";
            if (method != "Runtime.evaluate")
                return (new JsonObject(), null);
            lock (expressions)
                expressions.Add(expression);
            // Awaits: the probe comes back as 1, and the real call as its settled value.
            return (expression == "Promise.resolve(1)" ? ByValue(1) : ByValue(new JsonObject { ["ok"] = true }), null);
        });

        await using var console = new DevToolsConsole();
        await console.ConnectAsync(url);
        var value = await console.EvaluateValueAsync("A2SStore.status()");

        Assert.True(value?["ok"]?.GetValue<bool>());
        List<string> seen;
        lock (expressions)
            seen = expressions.ToList();
        Assert.DoesNotContain(seen, e => e.Contains("__a2s_eval_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StillFailsWhenRuntimeIsMissing()
    {
        var (url, _) = await StartInspectorAsync(new List<string>(), missing: "Runtime.enable");

        await using var console = new DevToolsConsole();
        await Assert.ThrowsAsync<InvalidOperationException>(() => console.ConnectAsync(url));
    }

    private static Task<(Uri Url, Task Server)> StartInspectorAsync(List<string> methods, string missing) =>
        StartInspectorAsync(methods, (method, _) => method == missing
            ? (null, $"'{method}' wasn't found")
            : (new JsonObject(), null));

    // A tiny inspector: the handler answers each command with a result or an error message. Plain
    // TCP with the WebSocket handshake done by hand, so the test needs no HTTP server.
    private static Task<(Uri Url, Task Server)> StartInspectorAsync(
        List<string> methods, Func<string, JsonObject?, (JsonNode? Result, string? Error)> handler)
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
                    var (result, error) = handler(method, request["params"]?.AsObject());
                    if (error is not null)
                        reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = error };
                    else
                        reply["result"] = result ?? new JsonObject();
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
