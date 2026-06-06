using Apps2Samsung.Helpers.Core;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin.Diagnostic
{
    public class JellyfinDiagnostic
    {
        public async Task InjectDevLogsAsync(PackageWorkspace ws)
        {
            string indexPath = Path.Combine(ws.Root, "www", "index.html");

            if (!File.Exists(indexPath)) return;

            var html = await File.ReadAllTextAsync(indexPath);

            // 2. CSP UPDATE: WebSockets (ws:) are often blocked by default CSP
            if (html.Contains("Content-Security-Policy"))
            {
                // Add ws: to the connect-src or default-src
                html = html.Replace("default-src", "connect-src * ws: wss:; default-src");
            }

            var script = new StringBuilder();
            script.AppendLine("<script>");
            script.AppendLine("(function(){");
            script.AppendLine("  try {");
            script.AppendLine($"    var ws = new WebSocket('ws://{AppSettings.Default.LocalIp}:54321');");
            script.AppendLine("    ws.onopen = function(){ ws.send('INJECTOR: websocket connected'); };");
            script.AppendLine("    ws.onerror = function(e){ alert('WS Error. Check Firewall on port 54321'); };");

            // ES5 Helper (No Arrow Functions)
            script.AppendLine("    var s = function(t, d) {");
            script.AppendLine("      try { if(ws.readyState === 1) ws.send(JSON.stringify({type:t, data:d})); } catch(e){}");
            script.AppendLine("    };");

            // console.log override (No Spread Operator)
            script.AppendLine("    var oldLog = console.log;");
            script.AppendLine("    console.log = function() {");
            script.AppendLine("      var args = Array.prototype.slice.call(arguments);");
            script.AppendLine("      if(oldLog) oldLog.apply(console, args);");
            script.AppendLine("      s('log', args);");
            script.AppendLine("    };");

            script.AppendLine("    window.onerror = function(m, sr, l, c) { s('error', [m, sr, l, c]); };");
            script.AppendLine("  } catch (e) { alert('Injector Failed: ' + e.message); }");
            script.AppendLine("})();");
            script.AppendLine("</script>");

            // Inject before </head>
            html = html.Replace("</head>", script.ToString() + "\n</head>");
            await File.WriteAllTextAsync(indexPath, html);
        }
    }
}
