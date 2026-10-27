using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.Core; // PackageWorkspace
using Apps2Samsung.Interfaces;    // IPackagePatcher
using Apps2Samsung.Models;        // InstallResult, UserSettingsSpec

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Writes the user's values for a community package's <c>user_settings</c> (#791) into the
    /// package before it is signed: each field an exact-text replacement, in the spec's one file or
    /// in every target the field lists (a port that lands in three files). Generic, driven
    /// by the catalog, so a new package of this kind needs no C#. Both heads ask for the values in a
    /// form before the install and keep them in <see cref="IAppConfig.AppUserSettingsJson"/>
    /// through <see cref="UserSettingsStore"/>; this patcher reads them back by package file name.
    /// </summary>
    public sealed class UserSettingsPatcher : IPackagePatcher
    {
        private readonly IAppConfig _config;

        public UserSettingsPatcher(IAppConfig config) => _config = config;

        public bool CanHandle(string packagePath) =>
            UserSettingsStore.Get(_config.AppUserSettingsJson, Path.GetFileName(packagePath)) is not null;

        public Task<InstallResult> ApplyAsync(PackageWorkspace ws)
        {
            var packageFile = Path.GetFileName(ws.PackagePath);
            var patch = UserSettingsStore.Get(_config.AppUserSettingsJson, packageFile);
            if (patch is null || patch.Fields.Count == 0)
                return Task.FromResult(InstallResult.SuccessResult());

            var root = Path.GetFullPath(ws.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            // One read and one write per file, in the order the spec names them.
            foreach (var file in patch.Edits().GroupBy(e => e.File, StringComparer.Ordinal))
            {
                var target = Path.GetFullPath(Path.Combine(ws.Root, file.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.Ordinal) || !File.Exists(target))
                    throw new UserSettingsPatchException(packageFile, file.Key, find: null);

                var text = File.ReadAllText(target);
                foreach (var edit in file)
                {
                    // A missing find means upstream changed the file: installing would hand the TV a
                    // package that silently can't connect, so stop here and say which text is gone.
                    if (!text.Contains(edit.Find, StringComparison.Ordinal))
                        throw new UserSettingsPatchException(packageFile, file.Key, edit.Find);
                    // Every occurrence: a value that appears twice in a config file is still one setting.
                    text = text.Replace(edit.Find, edit.Replace.Replace("{value}", UserSettingsStore.Escape(edit.Type, edit.Replace, edit.Value)), StringComparison.Ordinal);
                }
                File.WriteAllText(target, text);
                // Keys only: a value can be a credential (Telegram's API hash).
                System.Diagnostics.Trace.WriteLine($"[UserSettings] {packageFile}: wrote {string.Join(", ", file.Select(e => e.Key).Distinct())} into {file.Key}");
            }
            return Task.FromResult(InstallResult.SuccessResult());
        }
    }

    /// <summary>
    /// The package changed upstream (or the catalog's block is wrong): the text a field replaces is
    /// not in the file. The heads turn it into a message naming the package, the file and the text.
    /// </summary>
    public sealed class UserSettingsPatchException : Exception
    {
        public UserSettingsPatchException(string packageFile, string file, string? find)
            : base(find is null
                ? $"{packageFile}: {file} is not in the package."
                : $"{packageFile}: '{find}' is not in {file}.")
        {
            PackageFile = packageFile;
            File = file;
            Find = find;
        }

        public string PackageFile { get; }
        public string File { get; }
        /// <summary>The missing text; null when the file itself is missing.</summary>
        public string? Find { get; }
    }

    /// <summary>One field as stored: the spec's find/replace (or targets) with the user's value.</summary>
    public sealed class UserSettingsPatchField
    {
        public string Key { get; set; } = "";
        public string Type { get; set; } = "text";
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";
        /// <summary>Every place the value goes, when the spec lists targets instead of one file.</summary>
        public List<UserSettingsTarget>? Targets { get; set; }
        public string Value { get; set; } = "";
        /// <summary>
        /// The user left the field empty and <see cref="Value"/> is the spec's default: written into
        /// the package, but not offered back in the form, so a later default change still applies.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsDefault { get; set; }
    }

    /// <summary>One replacement the patcher makes: a file, the text in it, and what goes there.</summary>
    public sealed record UserSettingsEdit(string Key, string Type, string File, string Find, string Replace, string Value);

    /// <summary>What <see cref="IAppConfig.AppUserSettingsJson"/> holds for one package file.</summary>
    public sealed class UserSettingsPatch
    {
        public string File { get; set; } = "";
        public List<UserSettingsPatchField> Fields { get; set; } = new();

        /// <summary>The values the user typed, by key, for prefilling the form; defaults left out.</summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, string> Values =>
            Fields.Where(f => !f.IsDefault).ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);

        /// <summary>Every replacement, in spec order: a field's targets, or its find/replace in <see cref="File"/>.</summary>
        public IEnumerable<UserSettingsEdit> Edits() =>
            Fields.SelectMany(f => f.Targets is { Count: > 0 } targets
                ? targets.Select(t => new UserSettingsEdit(f.Key, f.Type, t.File, t.Find, t.Replace, f.Value))
                : new[] { new UserSettingsEdit(f.Key, f.Type, File, f.Find, f.Replace, f.Value) });
    }

    /// <summary>
    /// The JSON map behind <see cref="IAppConfig.AppUserSettingsJson"/>, keyed by package file name
    /// (stable across releases of a community package, and what the patcher matches on), plus the
    /// validation and escaping the form and the patcher share.
    /// </summary>
    public static class UserSettingsStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static Dictionary<string, UserSettingsPatch> Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, UserSettingsPatch>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, UserSettingsPatch>>(json, Json);
                return map is null
                    ? new Dictionary<string, UserSettingsPatch>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, UserSettingsPatch>(map, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return new Dictionary<string, UserSettingsPatch>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static string Write(Dictionary<string, UserSettingsPatch> map) =>
            map.Count == 0 ? string.Empty : JsonSerializer.Serialize(map, Json);

        public static UserSettingsPatch? Get(string? json, string packageFile) =>
            Read(json).TryGetValue(packageFile, out var patch) ? patch : null;

        /// <summary>
        /// Stores <paramref name="values"/> for <paramref name="packageFile"/> together with the
        /// current spec's find/replace pairs (or targets), so the install needs nothing but this
        /// JSON. A field the user left empty (allowed only when not required) takes the spec's
        /// default when it has one; without one it is left out, and its text untouched.
        /// </summary>
        public static string Set(string? json, string packageFile, UserSettingsSpec spec, IReadOnlyDictionary<string, string> values)
        {
            var map = Read(json);
            var patch = new UserSettingsPatch { File = spec.File };
            foreach (var field in spec.Fields)
            {
                var value = values.TryGetValue(field.Key, out var v) ? v.Trim() : string.Empty;
                var isDefault = value.Length == 0 && !string.IsNullOrWhiteSpace(field.Default);
                if (isDefault)
                    value = field.Default!.Trim();
                if (value.Length == 0)
                    continue;
                patch.Fields.Add(new UserSettingsPatchField
                {
                    Key = field.Key,
                    Type = field.Type,
                    Find = field.Find,
                    Replace = field.Replace,
                    Targets = field.Targets,
                    Value = value,
                    IsDefault = isDefault,
                });
            }
            map[packageFile] = patch;
            return Write(map);
        }

        /// <summary>Forgets a package's values (its catalog entry no longer declares any).</summary>
        public static string Remove(string? json, string packageFile)
        {
            var map = Read(json);
            return map.Remove(packageFile) ? Write(map) : json ?? string.Empty;
        }

        /// <summary>
        /// Why <paramref name="value"/> is not acceptable for <paramref name="field"/>, as a
        /// localization key, or null when it is. The heads show it under the field.
        /// </summary>
        public static string? Validate(UserSettingsField field, string? value)
        {
            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0)
                return field.Required ? "userSettingRequired" : null;
            if (field.IsNumber && !v.All(char.IsDigit))
                return "userSettingDigitsOnly";
            if (field.IsNumber && (field.Min is not null || field.Max is not null || field.Disallow is { Count: > 0 }))
            {
                // Digits only by now, so a failed parse is a number too large for any range.
                var inRange = long.TryParse(v, out var n)
                    && (field.Min is null || n >= field.Min)
                    && (field.Max is null || n <= field.Max);
                if (!inRange)
                    return field.Min is not null && field.Max is not null ? "userSettingRange"
                        : field.Min is not null ? "userSettingMin"
                        : "userSettingMax";
                if (field.Disallow?.Contains(n) == true)
                    return "userSettingNotAllowed";
            }
            if (!string.IsNullOrEmpty(field.Pattern))
            {
                try
                {
                    if (!Regex.IsMatch(v, field.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                        return "userSettingInvalidFormat";
                }
                catch (ArgumentException)
                {
                    // A broken pattern in the manifest is the manifest's problem, not the user's.
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }
            return null;
        }

        /// <summary>
        /// The text for a <see cref="Validate"/> key: <paramref name="localize"/>'s string with the
        /// field's <c>min</c> as {0} and <c>max</c> as {1}, for the range messages.
        /// </summary>
        public static string Message(UserSettingsField field, string key, Func<string, string> localize) =>
            string.Format(localize(key), field.Min, field.Max);

        /// <summary>
        /// The value as it goes into the file. <c>{value}</c> is substituted as-is, except that when
        /// the replacement text puts it inside a quoted string (the character right before
        /// <c>{value}</c> is <c>'</c> or <c>"</c>) a backslash and that quote in the value are
        /// escaped, which is right for both quoting styles. The manifests' patterns already keep
        /// quotes and backslashes out; this is the belt to that brace.
        /// </summary>
        public static string Escape(UserSettingsPatchField field, string value) =>
            Escape(field.Type, field.Replace, value);

        /// <inheritdoc cref="Escape(UserSettingsPatchField, string)"/>
        public static string Escape(string type, string replace, string value)
        {
            if (string.Equals(type, "number", StringComparison.OrdinalIgnoreCase))
                return value;
            var at = replace.IndexOf("{value}", StringComparison.Ordinal);
            var quote = at > 0 ? replace[at - 1] : '\0';
            if (quote != '\'' && quote != '"')
                return value;
            return value.Replace("\\", "\\\\").Replace(quote.ToString(), "\\" + quote);
        }
    }
}
