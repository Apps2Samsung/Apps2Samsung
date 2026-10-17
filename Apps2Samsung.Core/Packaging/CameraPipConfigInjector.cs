using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// One Camera PiP camera: a display name plus the two streams the app plays — <c>videoUrl</c>
    /// (H.264 + AAC HLS, decoded in hardware; full screen in lean mode and the leading mosaic tile)
    /// and <c>mjpegUrl</c> (the small mosaic tiles, the only pictures the app decodes itself).
    /// </summary>
    public sealed record CameraPipCamera(string Name, string VideoUrl, string MjpegUrl)
    {
        /// <summary>A row the user added and never filled in; skipped when writing the package.</summary>
        public bool IsBlank =>
            string.IsNullOrWhiteSpace(Name) &&
            string.IsNullOrWhiteSpace(VideoUrl) &&
            string.IsNullOrWhiteSpace(MjpegUrl);
    }

    /// <summary>
    /// The user's Camera PiP (dzikus/tizen-camera-pip) configuration: one to four cameras, the mosaic
    /// style and whether the lead camera plays sound. Everything else stays at the app's defaults.
    /// </summary>
    public sealed record CameraPipSettings(IReadOnlyList<CameraPipCamera> Cameras, string MosaicStyle, bool Sound)
    {
        public const string MosaicTiled = "tiled";
        public const string MosaicOverlay = "overlay";

        /// <summary>The app composes at most four cameras into its mosaic.</summary>
        public const int MaxCameras = 4;

        public static readonly CameraPipSettings Empty = new(Array.Empty<CameraPipCamera>(), MosaicTiled, true);

        /// <summary>True when no camera is configured, so the package should be left untouched.</summary>
        public bool IsEmpty => !Cameras.Any(c => !c.IsBlank);
    }

    /// <summary>
    /// Writes the user's cameras into a Camera PiP <c>.wgt</c>. The app reads one object,
    /// <c>window.PIP_CONFIG = {...};</c> in <c>config.js</c> at the package root, which upstream
    /// compiles from a YAML file at build time (tools/yaml2config.py) — the release package ships with
    /// <c>cameras: []</c> and shows a dark screen until that is edited and the app rebuilt in Docker.
    /// We edit it at install instead, the way <see cref="ImmiTvConfigInjector"/> fills ImmiTV's
    /// defaults. The existing object is kept and only the keys this form owns are replaced, so a build
    /// that ships other settings keeps them. Settings-agnostic: both heads pass their own
    /// <see cref="CameraPipSettings"/>, persisted as the JSON <see cref="ToJson"/> produces.
    /// </summary>
    public static class CameraPipConfigInjector
    {
        private const string ConfigJsRelativePath = "config.js";

        // `window.PIP_CONFIG = { ... };` — the object is JSON with nested braces, so the match runs
        // greedily to the last `};` in the file: config.js holds nothing after the assignment.
        private static readonly Regex ConfigObjectRegex = new(
            @"window\.PIP_CONFIG\s*=\s*(?<object>\{.*\})\s*;",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // config.js is a script, not HTML, so '&', '<' and '+' in a camera URL can stay as typed
        // instead of the default encoder's \u0026 escapes (valid JavaScript, unreadable config).
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>True if the package is a Camera PiP build (matched by wgt filename, e.g. CameraPip.wgt).</summary>
        public static bool AppliesTo(string packagePath)
        {
            var name = Path.GetFileName(packagePath);
            return name.Contains("camerapip", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("camera-pip", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses the persisted form: <c>{ "cameras": [{ "name", "videoUrl", "mjpegUrl" }], "mosaicStyle",
        /// "sound" }</c>. Empty settings on a blank or unreadable value. Reads through JsonNode rather
        /// than a typed deserialize, which is what survives the mobile head's trimmed build (#553).
        /// </summary>
        public static CameraPipSettings ParseSettingsJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return CameraPipSettings.Empty;

            try
            {
                if (JsonNode.Parse(json) is not JsonObject root)
                    return CameraPipSettings.Empty;

                var cameras = new List<CameraPipCamera>();
                if (root["cameras"] is JsonArray array)
                {
                    foreach (var item in array.OfType<JsonObject>())
                    {
                        cameras.Add(new CameraPipCamera(
                            ReadString(item, "name"),
                            ReadString(item, "videoUrl"),
                            ReadString(item, "mjpegUrl")));
                    }
                }

                var mosaic = NormalizeMosaicStyle(ReadString(root, "mosaicStyle"));
                var sound = root["sound"] is JsonValue soundValue && soundValue.TryGetValue<bool>(out var s) ? s : true;

                return new CameraPipSettings(cameras, mosaic, sound);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CameraPip] Failed to parse configured cameras: {ex.Message}");
                return CameraPipSettings.Empty;
            }
        }

        /// <summary>The persisted form of <paramref name="settings"/>; the inverse of <see cref="ParseSettingsJson"/>.</summary>
        public static string ToJson(CameraPipSettings settings)
        {
            var cameras = new JsonArray();
            foreach (var camera in settings.Cameras)
            {
                cameras.Add(new JsonObject
                {
                    ["name"] = camera.Name ?? string.Empty,
                    ["videoUrl"] = camera.VideoUrl ?? string.Empty,
                    ["mjpegUrl"] = camera.MjpegUrl ?? string.Empty,
                });
            }

            var root = new JsonObject
            {
                ["cameras"] = cameras,
                ["mosaicStyle"] = NormalizeMosaicStyle(settings.MosaicStyle),
                ["sound"] = settings.Sound,
            };
            return root.ToJsonString();
        }

        /// <summary>
        /// Rewrites <c>window.PIP_CONFIG</c> inside the package's <c>config.js</c>. No-op (leaves the
        /// workspace untouched) if no camera is configured, the file is missing, or the assignment
        /// isn't found. The caller owns the workspace and repacks once every patch is applied.
        /// </summary>
        public static async Task InjectAsync(PackageWorkspace ws, CameraPipSettings settings)
        {
            if (settings.IsEmpty)
            {
                Trace.WriteLine("[CameraPip] No cameras configured; leaving package unchanged.");
                return;
            }

            var configJsPath = Path.Combine(ws.Root, ConfigJsRelativePath);
            if (!File.Exists(configJsPath))
            {
                Trace.WriteLine($"[CameraPip] {ConfigJsRelativePath} not found in package; skipping.");
                return;
            }

            var js = await File.ReadAllTextAsync(configJsPath);
            var match = ConfigObjectRegex.Match(js);
            if (!match.Success)
            {
                Trace.WriteLine("[CameraPip] window.PIP_CONFIG assignment not found in config.js; skipping.");
                return;
            }

            var config = BuildConfigObject(match.Groups["object"].Value, settings);
            var payload = config.ToJsonString(WriteOptions);

            // MatchEvaluator, so a '$' in a camera URL isn't read as a regex substitution.
            js = ConfigObjectRegex.Replace(js, _ => $"window.PIP_CONFIG = {payload};", 1);

            await File.WriteAllTextAsync(configJsPath, js);
            Trace.WriteLine($"[CameraPip] Wrote {config["cameras"]!.AsArray().Count} camera(s) into {ConfigJsRelativePath} " +
                            $"(mosaicStyle={config["mosaicStyle"]}, sound={config["sound"]}).");
        }

        /// <summary>
        /// The object written into config.js: the package's own object with this form's keys replaced.
        /// A package whose object does not parse starts from an empty one — the keys the app needs are
        /// all set here, and every other key is optional upstream.
        /// </summary>
        internal static JsonObject BuildConfigObject(string existingObjectJson, CameraPipSettings settings)
        {
            JsonObject config;
            try
            {
                config = JsonNode.Parse(existingObjectJson) as JsonObject ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"[CameraPip] Existing PIP_CONFIG is not JSON ({ex.Message}); starting from an empty object.");
                config = new JsonObject();
            }

            var cameras = new JsonArray();
            foreach (var camera in settings.Cameras.Where(c => !c.IsBlank).Take(CameraPipSettings.MaxCameras))
            {
                // Only the keys with a value: the app treats a missing stream as "none", while an
                // empty string is a URL it would try to open.
                var entry = new JsonObject { ["name"] = (camera.Name ?? string.Empty).Trim() };
                if (!string.IsNullOrWhiteSpace(camera.VideoUrl))
                    entry["videoUrl"] = camera.VideoUrl.Trim();
                if (!string.IsNullOrWhiteSpace(camera.MjpegUrl))
                    entry["mjpegUrl"] = camera.MjpegUrl.Trim();
                cameras.Add(entry);
            }

            config["cameras"] = cameras;
            config["mosaicStyle"] = NormalizeMosaicStyle(settings.MosaicStyle);
            config["sound"] = settings.Sound;
            return config;
        }

        private static string NormalizeMosaicStyle(string? value) =>
            string.Equals(value?.Trim(), CameraPipSettings.MosaicOverlay, StringComparison.OrdinalIgnoreCase)
                ? CameraPipSettings.MosaicOverlay
                : CameraPipSettings.MosaicTiled;

        private static string ReadString(JsonObject obj, string key) =>
            obj[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : string.Empty;
    }
}
