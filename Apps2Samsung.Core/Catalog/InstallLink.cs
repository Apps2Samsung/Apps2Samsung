using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>What an install link asks for (#1066).</summary>
    public enum InstallLinkKind
    {
        /// <summary>Open the install sheet of one catalog app.</summary>
        Install,
        /// <summary>Open Add source on one independent repository, with its usual confirmation.</summary>
        Source,
    }

    /// <summary>
    /// A link a website, a README or another tool hands to Apps2Samsung (#1066):
    /// <list type="bullet">
    /// <item><c>apps2samsung://install?app=&lt;name|package id|app id&gt;[&amp;file=&lt;file or variant&gt;]</c>: a catalog app;</item>
    /// <item><c>apps2samsung://source?url=&lt;https url&gt;</c>: an independent repository to add;</item>
    /// <item>the same two on <c>https://apps2samsung.com/install</c> and <c>/source</c>, the page a README links to
    /// (GitHub strips custom schemes), which every head accepts as well.</item>
    /// </list>
    /// <c>app</c> takes the words TV-Store's own <c>app</c> key takes (TV-Store#102), so one name works in both.
    /// A link never carries a package URL: it can only name an app the catalog already lists, or a source
    /// the user then confirms. Nothing installs by itself; the heads open the normal sheet.
    /// </summary>
    public sealed record InstallLink(InstallLinkKind Kind, string? App = null, string? File = null, string? SourceUrl = null)
    {
        public const string Scheme = "apps2samsung";
        public const string WebHost = "apps2samsung.com";

        private const int MaxLinkLength = 2048;
        private const int MaxValueLength = 200;

        /// <summary>Whether <paramref name="text"/> looks like an install link at all (so a caller can say "bad link" rather than "no such app").</summary>
        public static bool LooksLikeLink(string? text)
        {
            var t = text?.Trim();
            if (string.IsNullOrEmpty(t))
                return false;
            return t.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://" + WebHost + "/", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://www." + WebHost + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads an install link. False for anything else: another scheme or host, an unknown action,
        /// a missing or overlong <c>app</c>, a source URL that is not absolute https.
        /// </summary>
        public static bool TryParse(string? text, out InstallLink? link)
        {
            link = null;
            var t = text?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length > MaxLinkLength)
                return false;

            string action;
            string query;
            if (t.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                // apps2samsung://install?..., and the forms some launchers pass on: apps2samsung:install?...,
                // apps2samsung:///install?..., with or without a trailing slash before the query.
                var rest = t.Substring(Scheme.Length + 1).TrimStart('/');
                var q = rest.IndexOf('?');
                action = (q < 0 ? rest : rest.Substring(0, q)).TrimEnd('/');
                query = q < 0 ? "" : rest.Substring(q + 1);
            }
            else
            {
                if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    return false;
                var host = uri.Host;
                if (!host.Equals(WebHost, StringComparison.OrdinalIgnoreCase) && !host.Equals("www." + WebHost, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!uri.IsDefaultPort)
                    return false;
                action = uri.AbsolutePath.Trim('/');
                query = uri.Query.TrimStart('?');
            }

            // A fragment is for the page, never part of a value.
            var hash = query.IndexOf('#');
            if (hash >= 0)
                query = query.Substring(0, hash);
            var values = ParseQuery(query);
            if (values is null)
                return false;

            switch (action.ToLowerInvariant())
            {
                case "install":
                {
                    var app = Value(values, "app");
                    if (app is null)
                        return false;
                    var file = values.ContainsKey("file") ? Value(values, "file") : null;
                    if (values.ContainsKey("file") && file is null)
                        return false;
                    link = new InstallLink(InstallLinkKind.Install, App: app, File: file);
                    return true;
                }
                case "source":
                {
                    var url = Value(values, "url");
                    if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var src) || src.Scheme != Uri.UriSchemeHttps
                        || string.IsNullOrEmpty(src.Host) || !string.IsNullOrEmpty(src.UserInfo))
                        return false;
                    link = new InstallLink(InstallLinkKind.Source, SourceUrl: src.ToString());
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>The link in its <c>apps2samsung://</c> form.</summary>
        public string ToSchemeUrl() => Scheme + "://" + Path();

        /// <summary>The link on the website, for a README.</summary>
        public string ToWebUrl() => "https://" + WebHost + "/" + Path();

        private string Path() => Kind switch
        {
            InstallLinkKind.Source => "source?url=" + Uri.EscapeDataString(SourceUrl ?? ""),
            _ => "install?app=" + Uri.EscapeDataString(App ?? "") + (File is null ? "" : "&file=" + Uri.EscapeDataString(File)),
        };

        /// <summary>
        /// The catalog app an install link names, among the entries the user can see (so an 18+ app
        /// stays hidden without the toggle, as in a search). <c>app</c> matches a catalog name exactly
        /// (case-insensitive), else a package id or app id of one of its files; never a part of a
        /// name, since a link is written once and the catalog changes under it.
        /// <para><c>File</c> on the result is the file the link pins: its <c>file</c> value, else the
        /// file an id names when only one file of the app carries that id; null leaves the app's default.</para>
        /// <para>Several sources can offer one name (#1018): every one is returned, the head asks which.</para>
        /// </summary>
        public static InstallLinkMatch? Resolve(InstallLink link, IEnumerable<GitHubRelease> entries)
        {
            if (link.Kind != InstallLinkKind.Install || string.IsNullOrWhiteSpace(link.App))
                return null;
            var all = entries.ToList();
            var app = link.App.Trim();

            var byName = all.Where(e => string.Equals(e.Name, app, StringComparison.OrdinalIgnoreCase)).ToList();
            string? idFile = null;
            var matches = byName;
            if (matches.Count == 0)
            {
                matches = all.Where(e => e.Assets.Any(a => IsId(a, app))).ToList();
                if (matches.Count == 0)
                    return null;
                // One name only: an id two different apps share (a fork that kept upstream's id, #1028)
                // does not say which one, so the link names nothing.
                if (matches.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    return null;
                var files = matches.SelectMany(e => e.Assets).Where(a => IsId(a, app))
                    .Select(a => a.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 1)
                    idFile = files[0];
            }

            var name = matches[0].Name;
            if (link.File is { } wanted)
            {
                // The file must exist on at least one source's entry, by file name, file name without
                // extension, or variant, as the CLI's --file reads it.
                var withFile = matches.Where(e => e.Assets.Any(a => FileMatches(a, wanted))).ToList();
                if (withFile.Count == 0)
                    return new InstallLinkMatch(name, matches, null, FileMissing: true);
                var fileName = withFile.SelectMany(e => e.Assets).First(a => FileMatches(a, wanted)).FileName;
                return new InstallLinkMatch(name, withFile, fileName, FileMissing: false);
            }
            return new InstallLinkMatch(name, matches, idFile, FileMissing: false);
        }

        /// <summary>Whether <paramref name="asset"/> is the file <paramref name="wanted"/> names.</summary>
        public static bool FileMatches(Asset asset, string wanted) =>
            string.Equals(asset.FileName, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileNameWithoutExtension(asset.FileName), wanted, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(asset.Variant) && string.Equals(asset.Variant, wanted, StringComparison.OrdinalIgnoreCase));

        private static bool IsId(Asset a, string id) =>
            (!string.IsNullOrWhiteSpace(a.PackageId) && string.Equals(a.PackageId, id, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(a.AppId) && string.Equals(a.AppId, id, StringComparison.Ordinal));

        private static string? Value(Dictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out var v))
                return null;
            v = v.Trim();
            if (v.Length == 0 || v.Length > MaxValueLength || v.Any(char.IsControl))
                return null;
            return v;
        }

        /// <summary>The query's values; null when a key repeats (a link that says two things says nothing).</summary>
        private static Dictionary<string, string>? ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var key = Decode(eq < 0 ? part : part.Substring(0, eq));
                var value = eq < 0 ? "" : Decode(part.Substring(eq + 1));
                if (key is null || value is null)
                    return null;
                if (!values.TryAdd(key, value))
                    return null;
            }
            return values;
        }

        private static string? Decode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch (UriFormatException) { return null; }
        }
    }

    /// <summary>
    /// What an install link found: the app's catalog name, every entry under it (one per source that
    /// offers it), the file the link pins (null for the default), and whether the link's <c>file</c>
    /// is one the app does not have (the head then says so and offers the app's default instead).
    /// </summary>
    public sealed record InstallLinkMatch(string Name, IReadOnlyList<GitHubRelease> Entries, string? File, bool FileMissing);
}
