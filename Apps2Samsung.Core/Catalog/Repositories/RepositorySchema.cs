using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Catalog.Repositories
{
    /// <summary>
    /// The rules of tizen-community-packages' <c>spec/repository.schema.json</c> (v1), written out:
    /// Core has no JSON Schema library and this one schema does not need one. Unknown fields are
    /// allowed, as the spec says. Returns the first problem as "path: message", or null.
    /// </summary>
    internal static class RepositorySchema
    {
        private static readonly Regex RepositoryId = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.Compiled);
        private static readonly Regex Line = new(@"^[^\n\r|]+$", RegexOptions.Compiled);
        private static readonly Regex OptionalLine = new(@"^[^\n\r|]*$", RegexOptions.Compiled);
        private static readonly Regex HttpsUrl = new(@"^https://[^\s/@:]+(?::\d+)?(?:/[^\s]*)?$", RegexOptions.Compiled);
        private static readonly Regex Sha256 = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled);
        private static readonly Regex DateTime = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled);
        private static readonly Regex ProviderId = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.Compiled);
        private static readonly Regex AssetName = new(@"^[^/\\]+[.](?:wgt|tpk)$", RegexOptions.Compiled);
        private static readonly Regex TizenVersion = new(@"^\d+(?:\.\d+)*$", RegexOptions.Compiled);
        private static readonly string[] Categories = { "media", "iptv", "streaming", "games", "casting", "tools", "other" };
        private static readonly string[] Flags = { "root", "closed-source", "self-updating" };
        private static readonly string[] CertLevels = { "public", "partner" };

        private sealed class Invalid : Exception
        {
            public Invalid(string message) : base(message) { }
        }

        public static string? Check(JsonObject manifest)
        {
            try
            {
                Manifest(manifest);
                return null;
            }
            catch (Invalid ex)
            {
                return ex.Message;
            }
        }

        private static void Manifest(JsonObject m)
        {
            Require(m, "(top level)", "schemaVersion", "repository", "generatedAt", "providers");
            var repo = Obj(m["repository"], "repository");
            Require(repo, "repository", "id", "name", "maintainer");
            Str(repo["id"], "repository/id", RepositoryId, max: 128);
            Str(repo["name"], "repository/name", Line, max: 80);
            Str(repo["maintainer"], "repository/maintainer", Line, max: 80);
            OptStr(repo, "description", "repository", Line, max: 300);
            OptStr(repo, "homepage", "repository", HttpsUrl, max: 2048);
            Date(m["generatedAt"], "generatedAt");

            var providers = Arr(m["providers"], "providers", min: 1, max: 500);
            for (var i = 0; i < providers.Count; i++)
                Provider(Obj(providers[i], $"providers/{i}"), $"providers/{i}");
        }

        private static void Provider(JsonObject p, string at)
        {
            Require(p, at, "id", "category", "url", "releases");
            Str(p["id"], $"{at}/id", ProviderId);
            Enum(p["category"], $"{at}/category", Categories);
            Str(p["url"], $"{at}/url", HttpsUrl, max: 2048);
            OptStr(p, "displayName", at, OptionalLine, max: 80);
            OptStr(p, "prefix", at, null, max: 80);
            if (p["take"] is { } take && (!Int(take, out var t) || t < 1 || t > 20))
                throw new Invalid($"{at}/take: must be an integer from 1 to 20");
            if (p["buildInfo"] is { } bi)
            {
                var b = Obj(bi, $"{at}/buildInfo");
                OptStr(b, "name", $"{at}/buildInfo", Line, max: 80);
                OptStr(b, "description", $"{at}/buildInfo", Line, max: 300);
                OptStr(b, "previewImage", $"{at}/buildInfo", HttpsUrl, max: 2048);
            }
            OptStr(p, "source_url", at, HttpsUrl, max: 2048);
            if (p["adult"] is { } adult && adult.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new Invalid($"{at}/adult: must be true or false");
            if (p["flags"] is { } flags)
            {
                var list = Arr(flags, $"{at}/flags");
                var seen = list.Select((f, i) => { Enum(f, $"{at}/flags/{i}", Flags); return f!.GetValue<string>(); }).ToList();
                if (seen.Distinct(StringComparer.Ordinal).Count() != seen.Count)
                    throw new Invalid($"{at}/flags: has a value twice");
            }
            if (p["cert_level"] is { } cert)
                Enum(cert, $"{at}/cert_level", CertLevels);
            if (p["ok"] is { } ok && ok.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new Invalid($"{at}/ok: must be true or false");
            OptStr(p, "latestTag", at, null);

            var releases = Arr(p["releases"], $"{at}/releases", max: 20);
            for (var j = 0; j < releases.Count; j++)
                Release(Obj(releases[j], $"{at}/releases/{j}"), $"{at}/releases/{j}");
        }

        private static void Release(JsonObject r, string at)
        {
            Require(r, at, "tag_name", "assets");
            OptStr(r, "url", at, HttpsUrl, max: 2048);
            OptStr(r, "name", at, null, max: 200);
            Str(r["tag_name"], $"{at}/tag_name", null, min: 1, max: 200);
            if (r["published_at"] is { } published)
                Date(published, $"{at}/published_at");
            if (r["draft"] is { } draft && draft.GetValueKind() != JsonValueKind.False)
                throw new Invalid($"{at}/draft: a draft release is not allowed");
            var assets = Arr(r["assets"], $"{at}/assets", min: 1, max: 50);
            for (var k = 0; k < assets.Count; k++)
                Asset(Obj(assets[k], $"{at}/assets/{k}"), $"{at}/assets/{k}");
        }

        private static void Asset(JsonObject a, string at)
        {
            Require(a, at, "name", "browser_download_url", "size", "digest");
            Str(a["name"], $"{at}/name", AssetName, max: 200);
            Str(a["browser_download_url"], $"{at}/browser_download_url", HttpsUrl, max: 2048);
            if (!Int(a["size"], out var size) || size < 1 || size > 536870912)
                throw new Invalid($"{at}/size: must be an integer from 1 to 536870912");
            Str(a["digest"], $"{at}/digest", Sha256);
            OptStr(a, "package_version", at, null, max: 64);
            OptStr(a, "app_id", at, null, max: 128);
            OptStr(a, "package_id", at, null, max: 128);
            OptStr(a, "required_version", at, TizenVersion);
            OptStr(a, "icon", at, HttpsUrl, max: 2048);
            OptStr(a, "icon_digest", at, Sha256);
            if ((a["icon"] is null) != (a["icon_digest"] is null))
                throw new Invalid($"{at}: icon and icon_digest come together or not at all");
        }

        private static void Require(JsonObject o, string at, params string[] keys)
        {
            foreach (var key in keys)
                if (!o.ContainsKey(key) || o[key] is null)
                    throw new Invalid($"{at}: '{key}' is a required property");
        }

        private static JsonObject Obj(JsonNode? node, string at) =>
            node as JsonObject ?? throw new Invalid($"{at}: must be an object");

        private static JsonArray Arr(JsonNode? node, string at, int min = 0, int max = int.MaxValue)
        {
            var array = node as JsonArray ?? throw new Invalid($"{at}: must be an array");
            if (array.Count < min || array.Count > max)
                throw new Invalid($"{at}: must have {min} to {max} items");
            return array;
        }

        private static string Str(JsonNode? node, string at, Regex? pattern, int min = 0, int max = int.MaxValue)
        {
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                throw new Invalid($"{at}: must be a string");
            var s = v.GetValue<string>();
            if (s.Length < min || s.Length > max)
                throw new Invalid($"{at}: must be {min} to {max} characters");
            if (pattern is not null && !pattern.IsMatch(s))
                throw new Invalid($"{at}: '{Shorten(s)}' does not match {pattern}");
            return s;
        }

        private static void OptStr(JsonObject o, string key, string at, Regex? pattern, int max = int.MaxValue)
        {
            if (o.ContainsKey(key))
                Str(o[key], $"{at}/{key}", pattern, max: max);
        }

        private static void Enum(JsonNode? node, string at, string[] allowed)
        {
            var s = Str(node, at, null);
            if (!allowed.Contains(s, StringComparer.Ordinal))
                throw new Invalid($"{at}: '{Shorten(s)}' is not one of {string.Join(", ", allowed)}");
        }

        private static void Date(JsonNode? node, string at)
        {
            var s = Str(node, at, DateTime);
            if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                throw new Invalid($"{at}: '{s}' is not a date-time");
        }

        private static bool Int(JsonNode? node, out long value)
        {
            value = 0;
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
                return false;
            if (v.TryGetValue(out value))
                return true;
            // 5.0 is an integer to JSON Schema.
            if (v.TryGetValue(out double d) && d == Math.Floor(d) && d is >= long.MinValue and <= long.MaxValue)
            {
                value = (long)d;
                return true;
            }
            return false;
        }

        private static string Shorten(string s) => s.Length > 60 ? s[..57] + "..." : s;
    }
}
