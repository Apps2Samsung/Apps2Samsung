using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Packaging
{
    /// <summary>One TVApp channel entry — a display name and an m3u8 (or other) stream URL.</summary>
    public sealed record TvChannel(string Name, string Url);

    /// <summary>
    /// Injects a user's TVApp channel list into a TVApp <c>.wgt</c> by rewriting the placeholder
    /// <c>var channels = [...]</c> array in <c>js/main.js</c>. Settings-agnostic (channels are passed
    /// in), so both the desktop and mobile heads reuse it — the desktop reads them from its
    /// <c>AppSettings</c>, mobile from its own settings.
    /// </summary>
    public static class TvAppChannelInjector
    {
        private const string MainJsRelativePath = "js/main.js";

        // Matches `var channels = [ ... ];` (smallest span, across newlines).
        private static readonly Regex ChannelsArrayRegex =
            new(@"var\s+channels\s*=\s*\[.*?\];", RegexOptions.Singleline | RegexOptions.Compiled);

        // Matches the upstream `var hls = new Hls()` construction inside loadChannel().
        private static readonly Regex HlsConstructRegex =
            new(@"var\s+hls\s*=\s*new\s+Hls\s*\(\s*\)", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        /// <summary>True if the package is a TVApp build (matched by wgt filename).</summary>
        public static bool AppliesTo(string packagePath)
            => Path.GetFileName(packagePath).Contains("tvapp", StringComparison.OrdinalIgnoreCase);

        /// <summary>Parses a persisted JSON array of <c>{ name, url }</c> into channels (empty on error).</summary>
        public static IReadOnlyList<TvChannel> ParseChannelsJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<TvChannel>();

            try
            {
                var raw = JsonSerializer.Deserialize<List<ChannelDto>>(json, ReadOptions);
                if (raw is null)
                    return Array.Empty<TvChannel>();

                return raw.ConvertAll(c => new TvChannel(c.Name ?? string.Empty, c.Url ?? string.Empty));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TvApp] Failed to parse configured channels: {ex.Message}");
                return Array.Empty<TvChannel>();
            }
        }

        /// <summary>
        /// Rewrites the channels array inside the package's <c>js/main.js</c>. No-op (leaves the
        /// package untouched) if there are no channels, the file is missing, or the placeholder
        /// array isn't found.
        /// </summary>
        public static async Task InjectChannelsAsync(string packagePath, IReadOnlyList<TvChannel> channels)
        {
            if (channels.Count == 0)
            {
                Trace.WriteLine("[TvApp] No channels configured; leaving package unchanged.");
                return;
            }

            using var ws = PackageWorkspace.Extract(packagePath);

            var mainJsPath = Path.Combine(ws.Root, "js", "main.js");
            if (!File.Exists(mainJsPath))
            {
                Trace.WriteLine($"[TvApp] {MainJsRelativePath} not found in package; skipping channels.");
                return;
            }

            var js = await File.ReadAllTextAsync(mainJsPath);
            if (!ChannelsArrayRegex.IsMatch(js))
            {
                Trace.WriteLine("[TvApp] channels array not found in main.js; skipping channels.");
                return;
            }

            // Build the channels JSON by hand (lowercase { name, url } keys) instead of serializing an
            // anonymous type. Reflection-based serialization of anonymous types loses its property
            // metadata under the mobile head's trimmed/AOT release build, which emitted empty objects
            // (`[{},{}]`) and blackscreened the TVApp (#553). Serializing each string on its own stays
            // trim-safe and keeps the exact same escaping.
            var payload = "[" + string.Join(",", channels.Select(c =>
                $"{{\"name\":{JsonSerializer.Serialize(c.Name ?? string.Empty)},\"url\":{JsonSerializer.Serialize(c.Url ?? string.Empty)}}}")) + "]";

            // Replace via a MatchEvaluator so a literal '$' in a URL isn't interpreted as a regex
            // substitution (e.g. `$1`) in the replacement string.
            js = ChannelsArrayRegex.Replace(js, _ => $"var channels = {payload};", 1);

            js = PatchChannelSwitch(js);

            await File.WriteAllTextAsync(mainJsPath, js);
            ws.Repack();
            Trace.WriteLine($"[TvApp] Injected {channels.Count} channel(s) into {MainJsRelativePath}.");
        }

        /// <summary>
        /// Workaround for an upstream TVApp bug (KaashDev/TVapp): <c>loadChannel()</c> constructs a
        /// fresh <c>new Hls()</c> on every channel switch without destroying the previous one, so the
        /// <c>&lt;video&gt;</c> element stays bound to the first stream and every channel plays channel
        /// #1. Since we already rewrite main.js to inject channels, also make the Hls instance persist
        /// and tear it down before each switch. No-op once the app is fixed upstream (a <c>destroy(</c>
        /// call is present) or if the construction isn't found.
        /// </summary>
        private static string PatchChannelSwitch(string js)
        {
            if (js.Contains("destroy(", StringComparison.Ordinal) || !HlsConstructRegex.IsMatch(js))
                return js;

            js = HlsConstructRegex.Replace(js, _ =>
                "if (window.__a2sHls) { try { window.__a2sHls.destroy(); } catch (e) {} } " +
                "var hls = window.__a2sHls = new Hls()", 1);
            Trace.WriteLine("[TvApp] Applied channel-switch fix (destroy the previous Hls before loading the next).");
            return js;
        }

        private sealed class ChannelDto
        {
            public string? Name { get; set; }
            public string? Url { get; set; }
        }
    }
}
