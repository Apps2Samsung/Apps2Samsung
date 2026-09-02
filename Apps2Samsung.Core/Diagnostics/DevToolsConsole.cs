using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>How loud a console entry is, mapped from the protocol's several different level names.</summary>
    public enum ConsoleLevel
    {
        Debug,
        Log,
        Info,
        Warning,
        Error
    }

    /// <summary>One line in the console.</summary>
    /// <param name="Timestamp">When this device received it (the TV's clock is not trustworthy).</param>
    /// <param name="Level">Severity, for colouring and filtering.</param>
    /// <param name="Text">The rendered message.</param>
    /// <param name="Origin">Script and line it came from, when the protocol said; else null.</param>
    public sealed record ConsoleEntry(DateTimeOffset Timestamp, ConsoleLevel Level, string Text, string? Origin);

    /// <summary>
    /// A console attached to an app running on the TV, over the Chrome DevTools Protocol.
    ///
    /// This is the mobile head's answer to the desktop's <c>chrome://inspect</c> handoff: Chrome on
    /// Android has no inspect page, so rather than host a desktop-era DevTools frontend in a WebView,
    /// the app speaks the protocol itself and renders the two things worth having on a phone — the log
    /// stream and an expression evaluator.
    ///
    /// Only the <c>Log</c> and <c>Runtime</c> domains are used. Both long predate the Chrome 63-era
    /// inspector on Tizen, so this works across the TV generations the installer supports.
    /// </summary>
    public sealed class DevToolsConsole : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
        private readonly CancellationTokenSource _stopping = new();
        private Task? _receiveLoop;
        private int _nextId;

        /// <summary>Raised for every console line, off the UI thread — marshal before touching the UI.</summary>
        public event Action<ConsoleEntry>? EntryReceived;

        /// <summary>Raised once when the connection ends, with the reason (null = closed on request).</summary>
        public event Action<string?>? Disconnected;

        /// <summary>Attaches to a target from <see cref="DevToolsInspector.ListTargetsAsync"/>.</summary>
        public async Task ConnectAsync(Uri webSocketUrl, CancellationToken ct = default)
        {
            await _socket.ConnectAsync(webSocketUrl, ct);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stopping.Token));

            // Runtime carries console.* calls and uncaught exceptions; Log carries what the browser
            // itself reports (failed requests, CSP violations, deprecations) — the entries that explain
            // a blank screen when the app's own logging says nothing.
            await SendCommandAsync("Runtime.enable", null, ct);
            await SendCommandAsync("Log.enable", null, ct);
        }

        /// <summary>
        /// Evaluates <paramref name="expression"/> in the page and returns the result, rendered the
        /// same way a logged value is. Exceptions in the expression come back as text, not throws —
        /// a typo at the prompt is a result to display, not a failure of the console.
        /// </summary>
        public async Task<string> EvaluateAsync(string expression, CancellationToken ct = default)
        {
            var response = await SendCommandAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                // Lets the user await a promise and see the resolved value, and accept `$0`-free
                // convenience APIs the page itself defines.
                ["awaitPromise"] = true,
                ["includeCommandLineAPI"] = true,
            }, ct);

            if (response?["exceptionDetails"] is JsonNode failure)
                return RenderException(failure);

            return RenderRemoteObject(response?["result"]);
        }

        private async Task<JsonNode?> SendCommandAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null)
                message["params"] = parameters;

            var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
                await _sendLock.WaitAsync(ct);
                try
                {
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
                finally
                {
                    _sendLock.Release();
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
                return await completion.Task.WaitAsync(linked.Token);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            string? reason = null;

            try
            {
                while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    // A single protocol message can exceed the buffer (a big console.log of an object),
                    // so frames are accumulated until the message is complete.
                    using var message = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await _socket.ReceiveAsync(buffer, ct);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            reason = _socket.CloseStatusDescription is { Length: > 0 } described
                                ? described
                                : "The TV closed the inspector connection.";
                            return;
                        }

                        message.Write(buffer, 0, received.Count);
                    }
                    while (!received.EndOfMessage);

                    Dispatch(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
            }
            catch (OperationCanceledException)
            {
                // Disposing — not an error.
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                Trace.WriteLine($"[devtools] receive loop ended: {ex}");
            }
            finally
            {
                // Nothing more will answer, so release anyone still awaiting a command.
                foreach (var pending in _pending.Values)
                    pending.TrySetException(new IOException(reason ?? "The inspector connection closed."));

                Disconnected?.Invoke(reason);
            }
        }

        private void Dispatch(string json)
        {
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(json);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[devtools] unparseable message ({ex.Message}): {Truncate(json, 200)}");
                return;
            }

            if (message is null)
                return;

            // A reply to one of our commands.
            if (message["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id))
            {
                if (_pending.TryGetValue(id, out var completion))
                {
                    if (message["error"] is JsonNode error)
                        completion.TrySetException(new InvalidOperationException(
                            Str(error["message"]) ?? "The inspector rejected the command."));
                    else
                        completion.TrySetResult(message["result"]);
                }
                return;
            }

            var entry = ToEntry(Str(message["method"]), message["params"]);
            if (entry is not null)
                EntryReceived?.Invoke(entry);
        }

        private ConsoleEntry? ToEntry(string? method, JsonNode? parameters) => method switch
        {
            "Runtime.consoleAPICalled" => FromConsoleCall(parameters),
            "Log.entryAdded" => FromLogEntry(parameters?["entry"]),
            "Runtime.exceptionThrown" => new ConsoleEntry(
                DateTimeOffset.Now,
                ConsoleLevel.Error,
                RenderException(parameters?["exceptionDetails"]),
                Origin(parameters?["exceptionDetails"])),
            _ => null,
        };

        // console.log("a", 1, {b:2}) arrives as an array of remote objects, which are joined the way a
        // browser console joins them.
        private static ConsoleEntry FromConsoleCall(JsonNode? parameters)
        {
            var arguments = parameters?["args"] as JsonArray;
            var text = arguments is null
                ? string.Empty
                : string.Join(" ", arguments.Select(RenderRemoteObject));

            return new ConsoleEntry(
                DateTimeOffset.Now,
                ConsoleTypeToLevel(Str(parameters?["type"])),
                text,
                Origin(parameters));
        }

        private static ConsoleEntry FromLogEntry(JsonNode? entry)
        {
            var text = Str(entry?["text"]) ?? string.Empty;
            var url = Str(entry?["url"]);
            var origin = url is null ? null : $"{url}{LineSuffix(entry?["lineNumber"])}";

            return new ConsoleEntry(DateTimeOffset.Now, LogLevelToLevel(Str(entry?["level"])), text, origin);
        }

        private static ConsoleLevel ConsoleTypeToLevel(string? type) => type switch
        {
            "error" or "assert" => ConsoleLevel.Error,
            "warning" or "warn" => ConsoleLevel.Warning,
            "info" => ConsoleLevel.Info,
            "debug" or "trace" => ConsoleLevel.Debug,
            _ => ConsoleLevel.Log,
        };

        private static ConsoleLevel LogLevelToLevel(string? level) => level switch
        {
            "error" => ConsoleLevel.Error,
            "warning" => ConsoleLevel.Warning,
            "info" => ConsoleLevel.Info,
            "verbose" => ConsoleLevel.Debug,
            _ => ConsoleLevel.Log,
        };

        private static string RenderException(JsonNode? details)
        {
            // The fully-formatted message with stack lives on the thrown object when there is one;
            // `text` alone is usually just "Uncaught".
            var described = Str(details?["exception"]?["description"]);
            if (!string.IsNullOrWhiteSpace(described))
                return described!;

            var text = Str(details?["text"]);
            var value = RenderRemoteObject(details?["exception"]);
            return string.IsNullOrWhiteSpace(text) ? value : $"{text} {value}".Trim();
        }

        /// <summary>
        /// Renders a protocol RemoteObject the way a console would show it. Primitives print as
        /// themselves; an object that came back by value prints as JSON; one that stayed on the TV
        /// prints from its preview, falling back to the type name.
        /// </summary>
        private static string RenderRemoteObject(JsonNode? remote)
        {
            if (remote is null)
                return string.Empty;

            // Present whenever returnByValue worked or the value is a primitive.
            if (remote["value"] is JsonNode value)
                return Str(value) ?? value.ToJsonString();

            // NaN, Infinity, -0: not representable in JSON, so the protocol sends them as text.
            if (Str(remote["unserializableValue"]) is { } unserializable)
                return unserializable;

            if (Str(remote["type"]) == "undefined")
                return "undefined";

            if (remote["preview"] is JsonNode preview && RenderPreview(preview) is { } rendered)
                return rendered;

            // For functions and errors this is the source text or the stack, which is what's wanted.
            return Str(remote["description"]) ?? Str(remote["type"]) ?? string.Empty;
        }

        // An object kept on the TV still arrives with a shallow preview of its properties. Rendering it
        // is what makes console.log(someObject) useful rather than just printing "Object".
        private static string? RenderPreview(JsonNode preview)
        {
            if (preview["properties"] is not JsonArray properties)
                return null;

            var rendered = properties.Select(property =>
                $"{Str(property?["name"])}: {Str(property?["value"]) ?? Str(property?["type"])}");

            var body = string.Join(", ", rendered);
            if (Str(preview["overflow"]) == "true" || preview["overflow"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
                body += ", …";

            // Arrays read better in brackets; the protocol calls them out by subtype.
            return Str(preview["subtype"]) == "array" ? $"[{body}]" : $"{{{body}}}";
        }

        private static string? Origin(JsonNode? node)
        {
            var frame = (node?["stackTrace"]?["callFrames"] as JsonArray)?.FirstOrDefault();
            var url = Str(frame?["url"]);
            if (string.IsNullOrWhiteSpace(url))
                return null;

            // The protocol counts lines from zero; humans and editors count from one.
            return $"{url}{LineSuffix(frame?["lineNumber"], zeroBased: true)}";
        }

        private static string LineSuffix(JsonNode? lineNumber, bool zeroBased = false)
        {
            if (lineNumber is not JsonValue value || !value.TryGetValue<int>(out var line))
                return string.Empty;

            return $":{(zeroBased ? line + 1 : line)}";
        }

        private static string? Str(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text[..max] + "…";

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();

            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
                }
                catch
                {
                    // Closing politely is a courtesy; the socket is going away regardless.
                }
            }

            if (_receiveLoop is not null)
            {
                try { await _receiveLoop; } catch { /* already reported via Disconnected */ }
            }

            _socket.Dispose();
            _sendLock.Dispose();
            _stopping.Dispose();
        }
    }
}
