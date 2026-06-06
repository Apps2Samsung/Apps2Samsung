using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public class GitHubAuthHandler : DelegatingHandler
    {
        private readonly string? _token;

        public GitHubAuthHandler(string? token)
            : base(CreateInnerHandler())
        {
            _token = token;
        }

        private static HttpClientHandler CreateInnerHandler()
        {
            var handler = new HttpClientHandler();


            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            {
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                        return true;

                    var host = message.RequestUri?.Host ?? string.Empty;
                    if (host.EndsWith("samsung.com", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith("samsungqbe.com", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith("tizen.org", StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.TraceWarning($"[SSL] Accepting Samsung cert with validation issue on Linux: {host} ({errors})");
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
            if (!string.IsNullOrEmpty(_token) && IsGitHubRequest(request.RequestUri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }

            var response = await base.SendAsync(request, cancellationToken);

            // Token is expired or revoked — retry unauthenticated for public endpoints
            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                request.Headers.Authorization != null)
            {
                Trace.TraceWarning("[GitHubAuth] Token rejected (401) — retrying without authorization");
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
