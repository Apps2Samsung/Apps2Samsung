using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Helpers.Core
{
    public static class HtmlUtils
    {
        public static string EnsureBaseHref(string html)
        {
            if (html.Contains("<base", System.StringComparison.OrdinalIgnoreCase))
                return RegexPatterns.Html.BaseTag.Replace(html, "<base href=\".\">");

            return html.Replace("<head>", "<head><base href=\".\">");
        }
        public static string RewriteLocalPaths(string html)
        {
            return RegexPatterns.Html.LocalPaths.Replace(html, "$1=\"$2\"");
        }
        public static string CleanAndApplyCsp(string html)
        {
            html = RegexPatterns.Html.CspMeta.Replace(html, "");
            return html.Replace("</head>",
                "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src * 'unsafe-inline' 'unsafe-eval' data: blob:;\">\n</head>");
        }
        public static string EnsurePublicJsIsLast(string html)
        {
            const string tag = "<script src=\"plugin_cache/public.js\"></script>";
            if (!html.Contains(tag)) return html;

            html = html.Replace(tag, "");
            return html.Replace("</body>", tag + "\n</body>");
        }
        /// <summary>
        /// Puts an injected block into <paramref name="html"/> exactly once: it is wrapped in a marker
        /// comment carrying <paramref name="blockId"/>, and a block already carrying that id is replaced
        /// rather than added next to. Injection used to be a plain
        /// <c>html.Replace("&lt;/head&gt;", block + "&lt;/head&gt;")</c>, which is fine on a fresh package
        /// but stacks another copy every time the same package is patched again: a second auto-login
        /// script with a stale token, a second custom-CSS block, a second debug hook (#702).
        /// </summary>
        /// <param name="html">The document to patch.</param>
        /// <param name="blockId">Stable id for this injection point, e.g. <c>auto-login</c>.</param>
        /// <param name="block">The markup to inject.</param>
        /// <param name="closingTag">Tag to inject before, <c>&lt;/head&gt;</c> by default.</param>
        public static string InjectBlock(string html, string blockId, string block, string closingTag = "</head>")
        {
            var open = $"<!--a2s:{blockId}-->";
            var close = $"<!--/a2s:{blockId}-->";

            // Drop the previous copy, marker comments and all, wherever in the document it sits.
            var existing = new Regex(
                @$"{Regex.Escape(open)}[\s\S]*?{Regex.Escape(close)}\s*",
                RegexOptions.IgnoreCase);
            html = existing.Replace(html, string.Empty);

            if (string.IsNullOrWhiteSpace(block))
                return html;

            var marked = $"{open}\n{block}\n{close}\n";

            // No closing tag to anchor to (a hand-built index.html, or one we already stripped) — the
            // block still has to ship, so append it rather than dropping it silently.
            return html.Contains(closingTag, StringComparison.OrdinalIgnoreCase)
                ? ReplaceFirst(html, closingTag, marked + closingTag)
                : html + marked;
        }

        private static string ReplaceFirst(string haystack, string needle, string replacement)
        {
            var at = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? haystack : haystack.Remove(at, needle.Length).Insert(at, replacement);
        }

        public static string EscapeJsString(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            return html
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }
        public static string RemoveMarkdownTable(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;

            var tablePattern = @"(\|[^\n]+\|\s*\n)+";

            return Regex.Replace(html, tablePattern, string.Empty, RegexOptions.Multiline);
        }
        public static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;

            // Simple HTML stripping - replace common tags
            var text = html
                .Replace("<br>", "\n")
                .Replace("<br/>", "\n")
                .Replace("<br />", "\n")
                .Replace("</p>", "\n")
                .Replace("</li>", "\n")
                .Replace("<li>", "• ");

            // Remove all remaining HTML tags
            while (text.Contains('<') && text.Contains('>'))
            {
                var start = text.IndexOf('<');
                var end = text.IndexOf('>', start);
                if (end > start)
                    text = text.Remove(start, end - start + 1);
                else
                    break;
            }

            // Decode common HTML entities
            text = text
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'");

            // Clean up whitespace
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)));
        }
    }
}
