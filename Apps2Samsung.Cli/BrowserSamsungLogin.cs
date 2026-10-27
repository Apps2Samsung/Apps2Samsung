using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Samsung;

namespace Apps2Samsung.Cli;

/// <summary>
/// The Samsung account sign-in for a terminal: the sign-in URL goes to the system browser (and is
/// printed, for a machine without one or a session driven remotely), and a loopback listener on
/// the port Samsung's SignInGate posts the token to takes it from there. The same mechanism as the
/// desktop's Kestrel callback and the phone's in-app listener, without a framework in the loop.
/// </summary>
public sealed class BrowserSamsungLogin : ISamsungLoginService
{
    private readonly bool _openBrowser;
    private readonly TimeSpan _timeout;
    private readonly Action<string> _say;

    public BrowserSamsungLogin(bool openBrowser, TimeSpan timeout, Action<string> say)
    {
        _openBrowser = openBrowser;
        _timeout = timeout;
        _say = say;
    }

    private static string RedirectUri => $"http://{Constants.Samsung.LoopbackHost}:{SamsungOAuth.CallbackPort}{SamsungOAuth.CallbackPath}";

    public async Task<SamsungAuth> LoginAsync(CancellationToken cancellationToken = default)
    {
        // Dual-stack: a browser may resolve "localhost" to ::1, and an IPv4-only listener would
        // never see the POST (the phone head found this the hard way).
        var listener = new TcpListener(IPAddress.IPv6Any, SamsungOAuth.CallbackPort);
        listener.Server.DualMode = true;
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Port {SamsungOAuth.CallbackPort} is in use, so the sign-in cannot be received. Close the Apps2Samsung desktop app if it is signing in, then retry.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var url = SamsungOAuth.BuildAuthorizeUrl(RedirectUri);
        try
        {
            _say("A Samsung account sign-in is needed to create the signing certificate.");
            _say(_openBrowser ? "Opening the browser; sign in there, then come back here." : "Open this address in a browser on this machine and sign in:");
            _say(url);
            if (_openBrowser && !TryOpenBrowser(url))
                _say("The browser could not be opened; open the address above by hand.");

            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                var (method, path, body) = await ReadRequestAsync(stream, timeout.Token);

                string? state = null, code = null;
                foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    if (kv[0] == "state") state = WebUtility.UrlDecode(kv[1]);
                    if (kv[0] == "code") code = Uri.UnescapeDataString(kv[1]);
                }

                var isCallback = method == "POST" && path.StartsWith(SamsungOAuth.CallbackPath, StringComparison.Ordinal);
                var accepted = isCallback && SamsungOAuth.IsValidState(state) && !string.IsNullOrWhiteSpace(code);
                await RespondAsync(stream, accepted ? 200 : isCallback ? 400 : 404,
                    accepted ? "Signed in. You can close this tab and return to the terminal."
                    : isCallback ? "Invalid sign-in response." : "Not found", timeout.Token);
                if (!accepted)
                    continue;

                var auth = JsonSerializer.Deserialize<SamsungAuth>(code!, JsonSerializerOptionsProvider.Default)
                    ?? throw new InvalidOperationException("Samsung login returned an unparseable token.");
                auth.state = state!;
                return auth;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No sign-in arrived within {_timeout.TotalMinutes:0} minutes.");
        }
        finally
        {
            try { listener.Stop(); } catch { /* already stopped */ }
        }
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Minimal HTTP/1.1 request reader: request line + headers, then the Content-Length body.
    private static async Task<(string method, string path, string body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        var raw = sb.ToString();
        var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = headerEnd >= 0 ? raw[..headerEnd] : raw;
        var lines = head.Split("\r\n");
        var requestLine = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();
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

    private static async Task RespondAsync(NetworkStream stream, int status, string text, CancellationToken ct)
    {
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var resp = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                   $"Content-Length: {Encoding.UTF8.GetByteCount(text)}\r\nConnection: close\r\n\r\n{text}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct);
        await stream.FlushAsync(ct);
    }
}
