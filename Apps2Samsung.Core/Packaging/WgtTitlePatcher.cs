using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core; // PackageWorkspace

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Swaps a Tizen package's launcher title — the text inside config.xml's <c>&lt;name&gt;</c> element,
    /// which is what the TV shows under the app icon. Head-agnostic; the package is re-signed after
    /// patching so editing config.xml in the extracted workspace is enough. Mirrors
    /// <see cref="WgtIconPatcher"/> (which swaps the icon bytes) for the title.
    /// </summary>
    public static class WgtTitlePatcher
    {
        // <name ...>title</name> — allow attributes (e.g. xml:lang) and any inner text.
        private static readonly Regex NameRegex =
            new(@"(<name\b[^>]*>)(.*?)(</name>)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Sets the app title in the workspace's config.xml (first <c>&lt;name&gt;</c> element).
        /// No-op if config.xml is missing or has no <c>&lt;name&gt;</c>.</summary>
        public static async Task SwapAppTitleAsync(PackageWorkspace ws, string newTitle)
        {
            if (string.IsNullOrWhiteSpace(newTitle))
                return;

            var configPath = Path.Combine(ws.Root, "config.xml");
            if (!File.Exists(configPath))
                return;

            try
            {
                var content = await File.ReadAllTextAsync(configPath);
                if (!NameRegex.IsMatch(content))
                {
                    Trace.WriteLine("[WgtTitle] config.xml has no <name> element; leaving title unchanged.");
                    return;
                }

                var escaped = SecurityElement.Escape(newTitle.Trim());
                var updated = NameRegex.Replace(content,
                    m => m.Groups[1].Value + escaped + m.Groups[3].Value, 1);

                await File.WriteAllTextAsync(configPath, updated);
                Trace.WriteLine($"[WgtTitle] Set app title to '{newTitle.Trim()}'.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WgtTitle] Failed to set app title: {ex.Message}");
            }
        }
    }
}
