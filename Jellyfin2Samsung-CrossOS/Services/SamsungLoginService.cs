using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Apps2Samsung.Services
{
    public class SamsungLoginService
    {
        private IWebHost? _callbackServer;

        private string CallbackUrl =>
            $"http://{Constants.Samsung.LoopbackHost}:{Constants.Ports.SamsungLoginCallbackPort}{Constants.Samsung.CallbackPath}";

        public Action<SamsungAuth>? CallbackReceived;

        public static Task<SamsungAuth> PerformSamsungLoginAsync()
        {
            return PerformSamsungLoginAsync(CancellationToken.None);
        }

        public static async Task<SamsungAuth> PerformSamsungLoginAsync(CancellationToken cancellationToken)
        {
            var service = new SamsungLoginService();

            var tcs = new TaskCompletionSource<SamsungAuth>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            service.CallbackReceived += auth =>
            {
                tcs.TrySetResult(auth);
            };

            cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled(cancellationToken);
            });

            await service.StartCallbackServer();

            string loginUrl =
                $"{Constants.Samsung.SignInGateUrl}" +
                $"?locale=&clientId={Constants.Samsung.OAuthClientId}" +
                $"&redirect_uri={HttpUtility.UrlEncode(service.CallbackUrl)}" +
                $"&state={Constants.Samsung.OAuthState}" +
                $"&tokenType={Constants.Samsung.TokenType}";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = loginUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await service.StopCallbackServer();
                throw new InvalidOperationException("Failed to open system browser.", ex);
            }

            try
            {
                return await tcs.Task;
            }
            finally
            {
                await service.StopCallbackServer();
            }
        }

        public async Task StartCallbackServer()
        {
            _callbackServer = new WebHostBuilder()
                .UseKestrel()
                .UseUrls($"http://{Constants.Samsung.LoopbackHost}:{Constants.Ports.SamsungLoginCallbackPort}")
                .Configure(app =>
                {
                    app.Run(async context =>
                    {
                        if (context.Request.Path == Constants.Samsung.CallbackPath &&
                            context.Request.Method == "POST")
                        {
                            string body = await new StreamReader(context.Request.Body).ReadToEndAsync();

                            string? state = null;
                            string? codeEncoded = null;

                            var parts = body.Split('&', StringSplitOptions.RemoveEmptyEntries);

                            foreach (var part in parts)
                            {
                                var kv = part.Split('=', 2);
                                if (kv.Length != 2) continue;

                                if (kv[0] == "state")
                                    state = WebUtility.UrlDecode(kv[1]);

                                if (kv[0] == "code")
                                    codeEncoded = Uri.UnescapeDataString(kv[1]);
                            }

                            if (string.IsNullOrWhiteSpace(codeEncoded))
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await context.Response.WriteAsync("Invalid login response (missing code).");
                                return;
                            }

                            try
                            {
                                var auth = JsonSerializer.Deserialize<SamsungAuth>(
                                    codeEncoded,
                                    JsonSerializerOptionsProvider.Default);

                                if (auth != null)
                                {
                                    auth.state = state;
                                    CallbackReceived?.Invoke(auth);

                                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                                    await context.Response.WriteAsync("Login successful. You can close this window.");
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await context.Response.WriteAsync(
                                    $"[CallbackServer] JSON parse error: {ex.Message}\n\nDecoded JSON:\n{codeEncoded}");
                                return;
                            }

                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            await context.Response.WriteAsync("Invalid login response.");
                            return;
                        }

                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        await context.Response.WriteAsync("Not Found");
                    });
                })
                .Build();

            await _callbackServer.StartAsync();

            Trace.WriteLine(
                $"[SamsungLoginService] Bound to http://{Constants.Samsung.LoopbackHost}:{Constants.Ports.SamsungLoginCallbackPort}");
        }

        public async Task StopCallbackServer()
        {
            if (_callbackServer != null)
            {
                await _callbackServer.StopAsync();
                _callbackServer.Dispose();
                _callbackServer = null;
            }
        }
    }
}
