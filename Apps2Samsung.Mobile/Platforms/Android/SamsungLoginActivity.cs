using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apps2Samsung.Samsung;
using AndroidUri = Android.Net.Uri;
using AndroidWebView = Android.Webkit.WebView;

namespace Apps2Samsung.Mobile.Platforms.Android;

// Hosts the Samsung SignInGate in a WebView and captures the token it POSTs to the redirect_uri.
// A WebView URL-intercept can't read a POST body, so we run a tiny in-app loopback listener on
// :4794 (same role as the desktop Kestrel callback) and point the redirect at it. Proven on-device
// by the tizen-sdb Probe; this is that recipe wrapped behind SamsungLoginService.
[Activity(Label = "Samsung Login", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public class SamsungLoginActivity : Activity
{
    // Single-flight bridge to SamsungLoginService: it sets this before launching, the activity
    // completes it with the raw token JSON (or faults/cancels it).
    internal static TaskCompletionSource<string>? Pending;

    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var web = new AndroidWebView(this);
        web.Settings.JavaScriptEnabled = true;   // SignInGate needs JS
        web.Settings.DomStorageEnabled = true;
        web.Settings.DatabaseEnabled = true;
        web.Settings.JavaScriptCanOpenWindowsAutomatically = true;
        web.Settings.SetSupportMultipleWindows(true);
        // The final redirect goes to http://localhost from an https page — allow it.
        web.Settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
        // Samsung's account pages behave badly under the default "; wv" WebView UA.
        web.Settings.UserAgentString =
            "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/126.0.0.0 Mobile Safari/537.36";

        // Cross-domain login needs cookies, including third-party.
        CookieManager.Instance!.SetAcceptCookie(true);
        CookieManager.Instance.SetAcceptThirdPartyCookies(web, true);

        web.SetWebViewClient(new LoggingClient(msg => Fault(new InvalidOperationException(msg))));

        SetContentView(web);

        _ = Task.Run(() => ListenForCallbackAsync(_cts.Token));

        var redirect = $"http://localhost:{SamsungOAuth.CallbackPort}{SamsungOAuth.CallbackPath}";
        web.LoadUrl(SamsungOAuth.BuildAuthorizeUrl(redirect));
    }

    private async Task ListenForCallbackAsync(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, SamsungOAuth.CallbackPort);
            _listener.Start();

            while (!ct.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();

                var (method, path, body) = await ReadRequestAsync(stream, ct);

                const string page = "Login captured — return to the app.";
                var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n" +
                           $"Content-Length: {Encoding.UTF8.GetByteCount(page)}\r\nConnection: close\r\n\r\n{page}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct);
                await stream.FlushAsync(ct);

                if (method == "POST" && path.StartsWith(SamsungOAuth.CallbackPath, StringComparison.Ordinal))
                {
                    var code = ParseFormField(body, "code");
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        var tokenJson = Uri.UnescapeDataString(code);
                        RunOnUiThread(() =>
                        {
                            Pending?.TrySetResult(tokenJson);
                            Pending = null;
                            Finish();
                        });
                        return;
                    }
                }
            }
        }
        catch (System.OperationCanceledException) { /* activity closing */ }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            try { _listener?.Stop(); } catch { /* ignore */ }
        }
    }

    private void Fault(Exception ex) => RunOnUiThread(() =>
    {
        Pending?.TrySetException(ex);
        Pending = null;
        Finish();
    });

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

    protected override void OnDestroy()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        // If we're leaving without a captured token (user backed out), unblock the awaiter.
        Pending?.TrySetCanceled();
        Pending = null;
        base.OnDestroy();
    }

    // Keeps navigation inside the WebView and surfaces main-frame load failures (so a failed page
    // gives the real reason rather than a generic error screen).
    private sealed class LoggingClient(Action<string> log) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(AndroidWebView? view, IWebResourceRequest? request)
            => false; // let the WebView handle every navigation (incl. the localhost POST)

        public override void OnReceivedError(AndroidWebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame == true)
                log($"load error {(int?)error?.ErrorCode}: {error?.Description} @ {request?.Url}");
            base.OnReceivedError(view, request, error);
        }
    }
}
