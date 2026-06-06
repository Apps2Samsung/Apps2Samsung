using Apps2Samsung.Helpers.Core;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin.CSS
{
    /// <summary>
    /// Injects custom CSS into the Jellyfin web app.
    /// Supports both inline CSS and @import rules for external themes like ElegantFin or Ultrachromic.
    /// </summary>
    public class CustomCss
    {
        public async Task InjectAsync(PackageWorkspace ws)
        {
            var customCss = AppSettings.Default.CustomCss;

            if (string.IsNullOrWhiteSpace(customCss))
            {
                Trace.WriteLine("[InjectCustomCss] No custom CSS configured, skipping injection");
                return;
            }

            string indexPath = Path.Combine(ws.Root, "www", "index.html");
            if (!File.Exists(indexPath))
            {
                Trace.WriteLine("[InjectCustomCss] index.html not found");
                return;
            }

            var html = await File.ReadAllTextAsync(indexPath);

            var cssBlock = new StringBuilder();
            cssBlock.AppendLine("<style id=\"jellyfin-custom-css\">");
            cssBlock.AppendLine(customCss);
            cssBlock.AppendLine("</style>");

            // Inject before </head> to ensure CSS is loaded with the page
            html = html.Replace("</head>", cssBlock + "</head>");

            await File.WriteAllTextAsync(indexPath, html);
            Trace.WriteLine("[InjectCustomCss] Custom CSS injected successfully");
        }
    }
}
