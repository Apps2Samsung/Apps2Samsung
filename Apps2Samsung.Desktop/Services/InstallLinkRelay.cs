using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Catalog;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Install links (#1066) reaching the desktop app. Windows and Linux start a new process with the
    /// link as its argument; when Apps2Samsung already runs, that process hands the link over a named
    /// pipe (a Unix socket on Linux, for this user only) and exits, so the open window takes it.
    /// macOS hands it to the running app itself (App.axaml.cs). Every link ends in <see cref="Deliver"/>,
    /// which keeps it until the main window takes it (<see cref="LinkArrived"/>, <see cref="TakePending"/>).
    /// </summary>
    public static class InstallLinkRelay
    {
        private const int MaxLink = 4096;
        private static readonly object Gate = new();
        private static string? _pending;
        private static Action<string>? _linkArrived;

        /// <summary>One pipe per user, so two people on one PC never reach each other's window.</summary>
        private static string PipeName
        {
            get
            {
                var user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..12];
                return "apps2samsung-links-" + user.ToLowerInvariant();
            }
        }

        /// <summary>The first argument that looks like an install link, if any.</summary>
        public static string? FromArgs(string[] args)
        {
            foreach (var a in args)
                if (InstallLink.LooksLikeLink(a))
                    return a.Trim();
            return null;
        }

        /// <summary>Hands <paramref name="link"/> to an Apps2Samsung that already runs; false when none answers.</summary>
        public static bool TrySend(string link)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(700);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                writer.WriteLine(link.Length > MaxLink ? link[..MaxLink] : link);
                writer.Flush();
                Trace.WriteLine("[links] handed the link to the running app");
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Listens for links from later starts, for as long as the app runs. A second listener (another window of the same user) just stays out.</summary>
        public static void StartListening()
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                            PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                        server.WaitForConnection();
                        using var reader = new StreamReader(server, Encoding.UTF8);
                        var buffer = new char[MaxLink];
                        var read = reader.ReadBlock(buffer, 0, buffer.Length);
                        var line = new string(buffer, 0, read).Split('\n')[0].Trim();
                        if (InstallLink.LooksLikeLink(line))
                            Deliver(line);
                    }
                    catch (IOException ex)
                    {
                        // Another Apps2Samsung of this user holds the pipe: that one takes the links.
                        Trace.WriteLine($"[links] not listening: {ex.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[links] listener: {ex.Message}");
                        Thread.Sleep(1000);
                    }
                }
            })
            { IsBackground = true, Name = "install-links" };
            thread.Start();
        }

        /// <summary>A link for the main window: raised at once when it listens, else kept for <see cref="TakePending"/>. A newer link replaces one not yet taken.</summary>
        public static void Deliver(string link)
        {
            Action<string>? handler;
            lock (Gate)
            {
                handler = _linkArrived;
                if (handler is null)
                    _pending = link;
            }
            Trace.WriteLine("[links] link received");
            handler?.Invoke(link);
        }

        /// <summary>The main window's handler; a link that came before it is passed on at once.</summary>
        public static void Subscribe(Action<string> handler)
        {
            string? pending;
            lock (Gate)
            {
                _linkArrived = handler;
                pending = _pending;
                _pending = null;
            }
            if (pending is not null)
                handler(pending);
        }
    }
}
