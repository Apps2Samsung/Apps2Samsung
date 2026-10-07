using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// The user's ImmiTV (xHiqhAim/immitv) connection defaults: the Immich server plus either an
    /// email/password pair or an API key. Every field is optional — empty strings are written as
    /// <c>''</c>, which makes the app's setup screen ask for them.
    /// </summary>
    public sealed record ImmiTvDefaults(string ServerUrl, string Email, string Password, string ApiKey)
    {
        public static readonly ImmiTvDefaults Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);

        /// <summary>True when nothing is configured, so the package should be left untouched.</summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(ServerUrl) &&
            string.IsNullOrWhiteSpace(Email) &&
            string.IsNullOrWhiteSpace(Password) &&
            string.IsNullOrWhiteSpace(ApiKey);
    }

    /// <summary>
    /// Injects the user's Immich connection details into an ImmiTV <c>.wgt</c> by rewriting the
    /// <c>var IMMICH_DEFAULTS = { ... };</c> object in <c>js/config.js</c>. ImmiTV reads that object
    /// to pre-fill its setup screen on first run (upstream expects you to edit it before building;
    /// we do it at install instead). Settings-agnostic like <see cref="TvAppChannelInjector"/>:
    /// the defaults are passed in, so both heads reuse it.
    /// </summary>
    public static class ImmiTvConfigInjector
    {
        private const string ConfigJsRelativePath = "js/config.js";

        // Matches `var IMMICH_DEFAULTS = { ... };` (smallest span, across newlines). The upstream
        // object has no nested braces, so a lazy match up to the first `};` is exact.
        private static readonly Regex DefaultsObjectRegex =
            new(@"var\s+IMMICH_DEFAULTS\s*=\s*\{.*?\}\s*;", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>True if the package is an ImmiTV build (matched by wgt filename, e.g. ImmiTV.wgt).</summary>
        public static bool AppliesTo(string packagePath)
            => Path.GetFileName(packagePath).Contains("immitv", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Rewrites the defaults object inside the package's <c>js/config.js</c>. No-op (leaves the
        /// workspace untouched) if nothing is configured, the file is missing, or the object isn't
        /// found. The caller owns the workspace and repacks once every patch is applied.
        /// </summary>
        public static async Task InjectAsync(PackageWorkspace ws, ImmiTvDefaults defaults)
        {
            if (defaults.IsEmpty)
            {
                Trace.WriteLine("[ImmiTV] No connection details configured; leaving package unchanged.");
                return;
            }

            var configJsPath = Path.Combine(ws.Root, "js", "config.js");
            if (!File.Exists(configJsPath))
            {
                Trace.WriteLine($"[ImmiTV] {ConfigJsRelativePath} not found in package; skipping.");
                return;
            }

            var js = await File.ReadAllTextAsync(configJsPath);
            if (!DefaultsObjectRegex.IsMatch(js))
            {
                Trace.WriteLine("[ImmiTV] IMMICH_DEFAULTS object not found in config.js; skipping.");
                return;
            }

            // Serialize each string on its own (trim-safe under the mobile head's AOT build — see
            // TvAppChannelInjector) and replace via a MatchEvaluator so a '$' in a password isn't
            // read as a regex substitution.
            var payload = BuildObjectLiteral(defaults);
            js = DefaultsObjectRegex.Replace(js, _ => $"var IMMICH_DEFAULTS = {payload};", 1);

            await File.WriteAllTextAsync(configJsPath, js);
            Trace.WriteLine($"[ImmiTV] Injected connection defaults into {ConfigJsRelativePath} " +
                            $"(server={!string.IsNullOrWhiteSpace(defaults.ServerUrl)}, email={!string.IsNullOrWhiteSpace(defaults.Email)}, " +
                            $"password={!string.IsNullOrWhiteSpace(defaults.Password)}, apiKey={!string.IsNullOrWhiteSpace(defaults.ApiKey)}).");
        }

        /// <summary>The JS object literal written into config.js, same key order as upstream.</summary>
        internal static string BuildObjectLiteral(ImmiTvDefaults defaults)
        {
            var fields = new List<string>
            {
                $"serverUrl: {JsonSerializer.Serialize((defaults.ServerUrl ?? string.Empty).Trim())}",
                $"email: {JsonSerializer.Serialize((defaults.Email ?? string.Empty).Trim())}",
                $"password: {JsonSerializer.Serialize(defaults.Password ?? string.Empty)}",
                $"apiKey: {JsonSerializer.Serialize((defaults.ApiKey ?? string.Empty).Trim())}",
            };
            return "{ " + string.Join(", ", fields) + " }";
        }
    }
}
