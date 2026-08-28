using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Remote
{
    /// <summary>
    /// Drives a Samsung TV's remote-control channel (<c>samsung.remote.control</c>) — the same
    /// WebSocket API Samsung's own apps use, and a different channel from the SDB link the installer
    /// runs on: it needs no Developer Mode, only that the TV is on the network and awake (#544).
    /// <para>
    /// Pairing: the first connection makes the TV show an "allow this device?" prompt, and the accept
    /// hands back a token. Reconnecting with that token is silent, so the token must be persisted per
    /// TV — without it every launch prompts again. <see cref="TokenIssued"/> fires when the TV grants
    /// or renews one; the caller stores it and passes it to the next constructor.
    /// </para>
    /// <para>
    /// Transport: 2016-and-later sets take <c>wss://…:8002</c> (self-signed certificate, accepted for
    /// this host only) and are the ones that use tokens; older sets take plain <c>ws://…:8001</c>.
    /// <see cref="ProbeAsync"/> reads <c>/api/v2/</c> to tell which, so the caller doesn't guess.
    /// </para>
    /// One client owns one connection; it is not thread-safe by itself, so key presses are serialized
    /// through a gate. Any send failure drops the connection, and the next call reconnects.
    /// </summary>
    public sealed class SamsungRemoteClient : IAsyncDisposable
    {
        private const int SecurePort = 8002;
        private const int InsecurePort = 8001;

        private readonly string _ip;
        private readonly string _clientName;
        private readonly bool _secure;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _receiveCts;
        private TaskCompletionSource<bool>? _handshake;

        public SamsungRemoteClient(string tvIpAddress, string clientName = "Apps2Samsung", string? token = null, bool secure = true)
        {
            _ip = tvIpAddress;
            _clientName = clientName;
            _secure = secure;
            Token = token;
        }

        /// <summary>The pairing token, if the TV has granted one. Persist it and feed it back in.</summary>
        public string? Token { get; private set; }

        /// <summary>Raised when the TV grants or renews a token — persist the value.</summary>
        public event Action<string>? TokenIssued;

        /// <summary>True while a connection is open.</summary>
        public bool IsConnected => _socket?.State == WebSocketState.Open;

        /// <summary>
        /// Reads <c>/api/v2/</c> to see whether the host is a Samsung TV that speaks the remote channel,
        /// and on which transport. Never throws — an unreachable or non-Samsung host comes back
        /// <see cref="SamsungRemoteCapability.Supported"/> = false.
        /// </summary>
        public static async Task<SamsungRemoteCapability> ProbeAsync(string tvIpAddress, CancellationToken cancellationToken = default)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var json = await http.GetStringAsync($"http://{tvIpAddress}:{InsecurePort}/api/v2/", cancellationToken);

                // A router/NAS admin UI also answers on 8001 with markup — don't parse that as JSON.
                var trimmed = json.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                    return SamsungRemoteCapability.Unsupported;

                var device = JsonNode.Parse(json)?["device"];
                if (device is null)
                    return SamsungRemoteCapability.Unsupported;

                // TokenAuthSupport ("true"/"false") marks the sets that want wss + a pairing token.
                var tokenAuth = device["TokenAuthSupport"]?.ToString();
                var powerState = device["PowerState"]?.ToString();

                return new SamsungRemoteCapability
                {
                    Supported = true,
                    UsesToken = string.Equals(tokenAuth, "true", StringComparison.OrdinalIgnoreCase),
                    Name = device["name"]?.ToString() ?? string.Empty,
                    Model = device["modelName"]?.ToString() ?? string.Empty,
                    // Absent on older sets; only "standby" is a definite "asleep".
                    IsAwake = !string.Equals(powerState, "standby", StringComparison.OrdinalIgnoreCase),
                    // Reported for wired sets too, despite the name. Worth caching: it is what a
                    // later Wake-on-LAN needs, and a sleeping TV won't tell us any more (#544).
                    MacAddress = device["wifiMac"]?.ToString() ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[remote] probe of {tvIpAddress} failed: {ex.Message}");
                return SamsungRemoteCapability.Unsupported;
            }
        }

        /// <summary>
        /// Opens the channel, waiting for the TV's accept. On a first (unpaired) connection this is
        /// where the TV shows its prompt, so allow for the user walking to the TV — the caller decides
        /// how long via <paramref name="cancellationToken"/>. Returns false if the TV refused or never
        /// answered; the reason is traced.
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsConnected)
                    return true;

                await DropAsync().ConfigureAwait(false);

                var socket = new ClientWebSocket();
                // The TV serves a self-signed certificate on 8002. Accept it for this connection only:
                // the endpoint is a fixed LAN address the user picked, and the alternative is no remote
                // at all on every set built since 2016.
                socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                _handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _socket = socket;

                await socket.ConnectAsync(BuildUri(), cancellationToken).ConfigureAwait(false);

                _receiveCts = new CancellationTokenSource();
                _ = ReceiveLoopAsync(socket, _receiveCts.Token);

                using var registration = cancellationToken.Register(() => _handshake?.TrySetResult(false));
                var accepted = await _handshake.Task.ConfigureAwait(false);
                if (!accepted)
                {
                    Trace.WriteLine($"[remote] {_ip} did not accept the connection (prompt declined, timed out, or unsupported).");
                    await DropAsync().ConfigureAwait(false);
                }

                return accepted;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[remote] connect to {_ip} failed: {ex.Message}");
                await DropAsync().ConfigureAwait(false);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Sends one key press (see <see cref="SamsungRemoteKeys"/>). Reconnects first if the channel
        /// has dropped. Returns false when the press could not be delivered.
        /// </summary>
        public Task<bool> SendKeyAsync(string key, CancellationToken cancellationToken = default) =>
            SendAsync(new JsonObject
            {
                ["method"] = "ms.remote.control",
                ["params"] = new JsonObject
                {
                    ["Cmd"] = "Click",
                    ["DataOfCmd"] = key,
                    ["Option"] = "false",
                    ["TypeOfRemote"] = "SendRemoteKey",
                },
            }, $"key {key}", cancellationToken);

        /// <summary>
        /// Types text into whatever field the TV has focused — the phone keyboard standing in for the
        /// on-screen one. Not every set implements it; a set that doesn't simply ignores the message,
        /// which is why this reports delivery, not that the text arrived.
        /// </summary>
        public Task<bool> SendTextAsync(string text, CancellationToken cancellationToken = default) =>
            SendAsync(new JsonObject
            {
                ["method"] = "ms.remote.control",
                ["params"] = new JsonObject
                {
                    ["Cmd"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                    ["DataOfCmd"] = "base64",
                    ["TypeOfRemote"] = "SendInputString",
                },
            }, "text input", cancellationToken);

        private async Task<bool> SendAsync(JsonObject payload, string what, CancellationToken cancellationToken)
        {
            if (!IsConnected && !await ConnectAsync(cancellationToken).ConfigureAwait(false))
                return false;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var socket = _socket;
                if (socket is null || socket.State != WebSocketState.Open)
                    return false;

                var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                // A TV that went to standby, or a Wi-Fi blip: drop the socket so the next press
                // reconnects (silently, since we hold the token) instead of failing forever.
                Trace.WriteLine($"[remote] {_ip} send of {what} failed: {ex.Message}");
                await DropAsync().ConfigureAwait(false);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        // Reads the channel: the accept/deny reply that completes the handshake, the token that comes
        // with it, and anything else the TV volunteers (traced, so a rejection is visible in the log).
        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                        break;

                    var message = Encoding.UTF8.GetString(buffer, 0, received.Count);
                    HandleMessage(message);
                }
            }
            catch (OperationCanceledException) { /* disconnecting */ }
            catch (Exception ex)
            {
                Trace.WriteLine($"[remote] {_ip} receive loop ended: {ex.Message}");
            }
            finally
            {
                // Never leave a caller waiting on a handshake that can no longer arrive.
                _handshake?.TrySetResult(false);
            }
        }

        private void HandleMessage(string message)
        {
            try
            {
                var node = JsonNode.Parse(message);
                var eventName = node?["event"]?.ToString();

                switch (eventName)
                {
                    case "ms.channel.connect":
                        // The token is only present on token-auth sets, and is renewed from time to time.
                        var token = node?["data"]?["token"]?.ToString();
                        if (!string.IsNullOrEmpty(token) && token != Token)
                        {
                            Token = token;
                            TokenIssued?.Invoke(token);
                        }
                        _handshake?.TrySetResult(true);
                        break;

                    case "ms.channel.unauthorized":
                        Trace.WriteLine($"[remote] {_ip} refused this device (prompt denied).");
                        _handshake?.TrySetResult(false);
                        break;

                    case "ms.channel.timeOut":
                        Trace.WriteLine($"[remote] {_ip} timed out waiting for the on-screen prompt.");
                        _handshake?.TrySetResult(false);
                        break;

                    default:
                        Trace.WriteLine($"[remote] {_ip} → {message}");
                        break;
                }
            }
            catch (JsonException)
            {
                Trace.WriteLine($"[remote] {_ip} sent a non-JSON message: {message}");
            }
        }

        private Uri BuildUri()
        {
            // The name shows up in the TV's device list and on the pairing prompt, base64 as the API
            // requires. The token, when we have one, is what makes the reconnect silent.
            var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(_clientName));
            var scheme = _secure ? "wss" : "ws";
            var port = _secure ? SecurePort : InsecurePort;
            var uri = $"{scheme}://{_ip}:{port}/api/v2/channels/samsung.remote.control?name={name}";

            if (!string.IsNullOrEmpty(Token))
                uri += $"&token={Token}";

            return new Uri(uri);
        }

        /// <summary>Closes the channel. The TV keeps the pairing, so reconnecting stays silent.</summary>
        public async Task DisconnectAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await DropAsync().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        private async Task DropAsync()
        {
            var socket = _socket;
            var cts = _receiveCts;
            _socket = null;
            _receiveCts = null;

            if (cts is not null)
            {
                try { cts.Cancel(); } catch { /* already done */ }
                cts.Dispose();
            }

            if (socket is not null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch { /* the TV may already be gone */ }
                socket.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            _gate.Dispose();
        }
    }

    /// <summary>What <see cref="SamsungRemoteClient.ProbeAsync"/> found out about a host.</summary>
    public sealed class SamsungRemoteCapability
    {
        public static SamsungRemoteCapability Unsupported => new();

        /// <summary>The host answered the Samsung TV API, so the remote channel should work.</summary>
        public bool Supported { get; init; }

        /// <summary>Set uses the token flow (2016+) — connect over wss and expect a pairing prompt.</summary>
        public bool UsesToken { get; init; }

        /// <summary>Reported friendly name, e.g. "[TV] Living Room".</summary>
        public string Name { get; init; } = string.Empty;

        public string Model { get; init; } = string.Empty;

        /// <summary>False when the TV reported standby — keys won't reach it until it is woken.</summary>
        public bool IsAwake { get; init; } = true;

        /// <summary>The TV's MAC, for <see cref="SamsungRemoteWake"/>. Only readable while awake.</summary>
        public string MacAddress { get; init; } = string.Empty;
    }
}
