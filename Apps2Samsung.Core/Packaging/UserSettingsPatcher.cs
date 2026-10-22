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
    /// package before it is signed: one file, each field an exact-text replacement. Generic, driven
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

            var target = Path.GetFullPath(Path.Combine(ws.Root, patch.File.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(ws.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.Ordinal) || !File.Exists(target))
                throw new UserSettingsPatchException(packageFile, patch.File, find: null);

            var text = File.ReadAllText(target);
            foreach (var field in patch.Fields)
            {
                // A missing find means upstream changed the file: installing would hand the TV a
                // package that silently can't connect, so stop here and say which text is gone.
                if (!text.Contains(field.Find, StringComparison.Ordinal))
                    throw new UserSettingsPatchException(packageFile, patch.File, field.Find);
                // Every occurrence: a value that appears twice in a config file is still one setting.
                text = text.Replace(field.Find, field.Replace.Replace("{value}", UserSettingsStore.Escape(field, field.Value)), StringComparison.Ordinal);
            }
            File.WriteAllText(target, text);
            // Keys only: a value can be a credential (Telegram's API hash).
            System.Diagnostics.Trace.WriteLine($"[UserSettings] {packageFile}: wrote {string.Join(", ", patch.Fields.Select(f => f.Key))} into {patch.File}");
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

    /// <summary>One field as stored: the spec's find/replace with the user's value.</summary>
    public sealed class UserSettingsPatchField
    {
        public string Key { get; set; } = "";
        public string Type { get; set; } = "text";
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>What <see cref="IAppConfig.AppUserSettingsJson"/> holds for one package file.</summary>
    public sealed class UserSettingsPatch
    {
        public string File { get; set; } = "";
        public List<UserSettingsPatchField> Fields { get; set; } = new();

        /// <summary>The stored values by key, for prefilling the form.</summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, string> Values =>
            Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
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
        /// current spec's find/replace pairs, so the install needs nothing but this JSON. Fields the
        /// user left empty (allowed only when not required) are left out, and their text untouched.
        /// </summary>
        public static string Set(string? json, string packageFile, UserSettingsSpec spec, IReadOnlyDictionary<string, string> values)
        {
            var map = Read(json);
            var patch = new UserSettingsPatch { File = spec.File };
            foreach (var field in spec.Fields)
            {
                var value = values.TryGetValue(field.Key, out var v) ? v.Trim() : string.Empty;
                if (value.Length == 0)
                    continue;
                patch.Fields.Add(new UserSettingsPatchField
                {
                    Key = field.Key,
                    Type = field.Type,
                    Find = field.Find,
                    Replace = field.Replace,
                    Value = value,
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
        /// The value as it goes into the file. <c>{value}</c> is substituted as-is, except that when
        /// the replacement text puts it inside a quoted string (the character right before
        /// <c>{value}</c> is <c>'</c> or <c>"</c>) a backslash and that quote in the value are
        /// escaped, which is right for both quoting styles. The manifests' patterns already keep
        /// quotes and backslashes out; this is the belt to that brace.
        /// </summary>
        public static string Escape(UserSettingsPatchField field, string value)
        {
            if (string.Equals(field.Type, "number", StringComparison.OrdinalIgnoreCase))
                return value;
            var at = field.Replace.IndexOf("{value}", StringComparison.Ordinal);
            var quote = at > 0 ? field.Replace[at - 1] : '\0';
            if (quote != '\'' && quote != '"')
                return value;
            return value.Replace("\\", "\\\\").Replace(quote.ToString(), "\\" + quote);
        }
    }
}
