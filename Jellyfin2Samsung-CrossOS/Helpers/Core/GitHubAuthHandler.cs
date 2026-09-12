using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public class GitHubAuthHandler : DelegatingHandler
    {
        private readonly string? _token;

        // Set once GitHub has answered 401 to the token. A bearer token GitHub rejects is expired,
        // revoked or mistyped, and stays that way for the rest of the run — so after the first
        // rejection every request goes out unauthenticated straight away instead of each one being
        // sent twice and logging the same warning (one launch produced dozens of those lines).
        private int _tokenRejected;

        /// <summary>True once the configured token has been rejected by GitHub this run.</summary>
        public bool TokenRejected => Volatile.Read(ref _tokenRejected) != 0;

        public GitHubAuthHandler(string? token)
            : base(CreateInnerHandler())
        {
            _token = token;
        }

        private static HttpClientHandler CreateInnerHandler()
        {
            var handler = new HttpClientHandler();

            // Explicitly offer modern TLS. Legacy stacks — notably Windows 7 — default to
            // TLS 1.0/1.1, which GitHub and Samsung now refuse at the handshake, surfacing as
            // "The SSL connection could not be established". Requesting 1.2/1.3 lets those TVs
            // negotiate TLS 1.2 (the highest Windows 7 SChannel supports when it's enabled).
            try
            {
                handler.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }
            catch
            {
                try { handler.SslProtocols = SslProtocols.Tls12; } catch { /* fall back to OS default */ }
            }

            // Accept chain/name validation issues for the specific hosts we talk to on platforms
            // whose trust store / TLS stack is unreliable: Linux (no unified store integration for
            // these) and legacy Windows (7/8 — stale root certificates). Scoped to our known hosts,
            // and only where the OS is already outside the supported set. Modern Windows/macOS keep
            // full validation.
            var legacyWindows = OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10);
            if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) || legacyWindows)
            {
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                        return true;

                    var host = message.RequestUri?.Host ?? string.Empty;
                    if (host.EndsWith("samsung.com", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith("samsungqbe.com", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith("tizen.org", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.TraceWarning($"[SSL] Accepting cert with validation issue for {host} ({errors})");
                        return true;
                    }

                    return false;
                };
            }

            return handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(_token) && !TokenRejected && IsGitHubRequest(request.RequestUri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }

            var response = await base.SendAsync(request, cancellationToken);

            // Token is expired or revoked — retry this request unauthenticated (the endpoints we use
            // are public) and stop offering the token for the rest of the run.
            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                request.Headers.Authorization != null)
            {
                if (Interlocked.Exchange(ref _tokenRejected, 1) == 0)
                {
                    Trace.TraceWarning("[GitHubAuth] GitHub rejected the token (401). Continuing without " +
                                       "authorization for this session (60 requests/hour). Replace or clear the " +
                                       "GitHub token in Settings, or GITHUB_TOKEN / gh auth, to authenticate again.");
                }
                response.Dispose();
                var retry = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var header in request.Headers)
                {
                    if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                        retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                response = await base.SendAsync(retry, cancellationToken);
            }

            return response;
        }

        private static bool IsGitHubRequest(Uri? uri)
        {
            if (uri == null) return false;
            var host = uri.Host;
            return host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        }

        public static string? ResolveToken(AppSettings settings)
        {
            // 1. Explicit setting
            if (!string.IsNullOrWhiteSpace(settings.GitHubToken))
            {
                Trace.TraceInformation("[GitHubAuth] Using token from app settings");
                return settings.GitHubToken.Trim();
            }

            // 2. Environment variable
            var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken))
            {
                Trace.TraceInformation("[GitHubAuth] Using token from GITHUB_TOKEN environment variable");
                return envToken.Trim();
            }

            // 3. GitHub CLI (gh auth token)
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "gh",
                    Arguments = "auth token",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        Trace.TraceInformation("[GitHubAuth] Using token from GitHub CLI (gh auth token)");
                        return output;
                    }
                }
            }
            catch
            {
                // gh CLI not installed or not authenticated — ignore
            }

            // 4. No token available — unauthenticated requests
            Trace.TraceInformation("[GitHubAuth] No token found — using unauthenticated requests");
            return null;
        }
    }
}
