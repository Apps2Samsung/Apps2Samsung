using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Apps2Samsung.Models;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Parses the raw <c>vd_applist</c> output (the same listing already used to detect whether an app
    /// is installed) into a list of <see cref="InstalledApp"/>, shared by both heads' "installed apps"
    /// view. The listing is a sequence of per-app blocks separated by long dash rules, each line being
    /// <c>--------------&lt;key&gt;   =&lt;value&gt;-------------</c>.
    /// </summary>
    public static class TizenInstalledApps
    {
        // A single "key = value" field line, tolerant of the surrounding dashes and alignment padding.
        // The value is non-greedy up to the trailing 3+ dashes (single dashes inside e.g. a date/path
        // don't terminate it).
        private static readonly Regex FieldLine = new(
            @"^\s*-{3,}\s*(?<key>[^\s=]+)\s*=\s*(?<val>.*?)\s*-{3,}\s*$",
            RegexOptions.Compiled);

        // A block-separator rule: a line of only dashes/whitespace (no '='), long enough to be a divider.
        private static bool IsSeparator(string line) =>
            !line.Contains('=') && line.Contains("----") && line.Replace("-", "").Trim().Length == 0;

        /// <summary>Parse the applist output. Returns the apps sorted by install size (largest first,
        /// so the biggest space users are on top), then display name (case-insensitive); empty when the
        /// output is blank or an error/"no listing" response.</summary>
        public static IReadOnlyList<InstalledApp> Parse(string? output)
        {
            var apps = new List<InstalledApp>();
            if (string.IsNullOrWhiteSpace(output))
                return apps;

            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Flush()
            {
                if (current.TryGetValue("app_tizen_id", out var id) && !string.IsNullOrWhiteSpace(id))
                {
                    long.TryParse(current.GetValueOrDefault("app_size", string.Empty).Trim(), out var size);
                    apps.Add(new InstalledApp(
                        Title: current.GetValueOrDefault("app_title", string.Empty),
                        TizenId: id.Trim(),
                        Version: current.GetValueOrDefault("app_version", string.Empty),
                        InstallDate: current.GetValueOrDefault("install_date", string.Empty),
                        IsRemovable: current.GetValueOrDefault("is_removable", string.Empty) == "1",
                        SizeBytes: size));
                }
                current.Clear();
            }

            foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                if (IsSeparator(raw))
                {
                    Flush();
                    continue;
                }

                var m = FieldLine.Match(raw);
                if (m.Success)
                    current[m.Groups["key"].Value.Trim()] = m.Groups["val"].Value.Trim();
            }
            Flush();

            return apps
                .GroupBy(a => a.TizenId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(a => a.SizeBytes)
                .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
