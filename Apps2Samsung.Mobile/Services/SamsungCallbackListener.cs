using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apps2Samsung.Samsung;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// The in-app loopback endpoint the Samsung SignInGate hands the token to. The gate finishes with a
/// form POST to the redirect_uri, and a WebView can't read a POST body, so each platform's login
/// screen points the redirect at <see cref="RedirectUri"/> and runs this listener next to the
/// WebView (same role as the desktop's Kestrel callback). Shared by the Android activity and the
/// iOS view controller; only the WebView hosting differs per platform.
/// </summary>
internal sealed class SamsungCallbackListener : IDisposable
{
    private readonly TcpListener _listener;

    public SamsungCallbackListener()
    {
        // Dual-stack: a mobile browser often resolves the redirect's "localhost" to the IPv6 loopback
        // (::1), so an IPv4-only (127.0.0.1) listener never receives the callback POST and the login
        // page hangs on signInComplete. Accepting both ::1 and 127.0.0.1 fixes it.
        _listener = new TcpListener(IPAddress.IPv6Any, SamsungOAuth.CallbackPort);
        _listener.Server.DualMode = true;
    }

    /// <summary>Where the SignInGate is told to send the token: this listener, over plain HTTP.</summary>
    public static string RedirectUri => $"http://localhost:{SamsungOAuth.CallbackPort}{SamsungOAuth.CallbackPath}";

    /// <summary>
    /// Serves requests until a POST to the callback path carries a <c>code</c> field, and returns that
    /// field URL-decoded: SignInGate's token bundle as JSON. Stops listening on return or cancellation.
    /// </summary>
    public async Task<string> WaitForTokenAsync(CancellationToken ct)
    {
        _listener.Start();
        Trace.WriteLine($"[SamsungLogin] callback listener up on :{SamsungOAuth.CallbackPort} (dual-stack)");
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var client = await _listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();

                var (method, path, body) = await ReadRequestAsync(stream, ct);
                Trace.WriteLine($"[SamsungLogin] callback request: {method} {path}");

                const string page = "Login captured — return to the app.";
                var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n" +
                           $"Content-Length: {Encoding.UTF8.GetByteCount(page)}\r\nConnection: close\r\n\r\n{page}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct);
                await stream.FlushAsync(ct);

                if (method == "POST" && path.StartsWith(SamsungOAuth.CallbackPath, StringComparison.Ordinal))
                {
                    var code = ParseFormField(body, "code");
                    if (!string.IsNullOrWhiteSpace(code))
                        return Uri.UnescapeDataString(code);
                }
            }
        }
        finally
        {
            Stop();
        }
    }

    // Minimal HTTP/1.1 request reader: request line + headers, then the Content-Length body.
    private static async Task<(string method, string path, string body)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        int headerEnd;

        while ((headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        }

        var raw = sb.ToString();
        headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = headerEnd >= 0 ? raw[..headerEnd] : raw;
        var lines = head.Split("\r\n");
        var requestLine = lines.Length > 0 ? lines[0].Split(' ') : new[] { "", "", "" };
        var method = requestLine.Length > 0 ? requestLine[0] : "";
        var path = requestLine.Length > 1 ? requestLine[1] : "";

        int contentLength = 0;
        foreach (var line in lines)
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out contentLength);

        var body = headerEnd >= 0 ? raw[(headerEnd + 4)..] : string.Empty;
        while (Encoding.UTF8.GetByteCount(body) < contentLength)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) break;
            body += Encoding.UTF8.GetString(buf, 0, n);
        }

        return (method, path, body);
    }

    private static string? ParseFormField(string body, string key)
    {
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return kv[1];
        }
        return null;
    }

    private void Stop()
    {
        try { _listener.Stop(); } catch { /* already stopped */ }
    }

    public void Dispose() => Stop();
}
