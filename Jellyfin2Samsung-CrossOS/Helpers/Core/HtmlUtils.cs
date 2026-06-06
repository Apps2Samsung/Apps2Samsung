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
