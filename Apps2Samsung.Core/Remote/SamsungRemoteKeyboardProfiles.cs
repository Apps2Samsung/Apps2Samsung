using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Apps2Samsung.Remote
{
    /// <summary>
    /// The keyboards the remote knows about, and the lookup from an app id to one of them.
    /// <para>
    /// Profiles are **data**: one JSON file per keyboard in <c>Remote/Keyboards/</c>, embedded in this
    /// assembly and parsed with <see cref="SamsungRemoteKeyboardJson"/>. Adding support for another app
    /// is therefore a file plus a test — no code change here — and a user can override any of them, or
    /// supply one for an app we don't ship, by pasting the same JSON into the remote
    /// (stored per app id in settings).
    /// </para>
    /// <para>
    /// A grid walk is only correct for the app its profile was measured on, so resolution is never
    /// guessed: an app with no profile resolves to null and the remote says it cannot type there rather
    /// than pressing YouTube's cells into something else.
    /// </para>
    /// </summary>
    public static class SamsungRemoteKeyboardProfiles
    {
        /// <summary>YouTube's app id on Tizen.</summary>
        public const string YouTubeAppId = "9Ur5IzDKqV.TizenYouTube";

        /// <summary>TizenTube standalone — YouTube's UI in its own package.</summary>
        public const string TizenTubeAppId = "xvvl3S1TT1.TizenTubeStandalone";

        /// <summary>What to assume when the caller has no specific app in mind.</summary>
        public const string DefaultAppId = YouTubeAppId;

        private const string ResourceMarker = ".Remote.Keyboards.";
        private static readonly Lazy<IReadOnlyList<SamsungRemoteKeyboardProfile>> Loaded = new(LoadBuiltIn);

        /// <summary>Profiles that ship with the app, in file order.</summary>
        public static IReadOnlyList<SamsungRemoteKeyboardProfile> BuiltIn => Loaded.Value;

        /// <summary>The built-in profile for an app, or null when we have none for it.</summary>
        public static SamsungRemoteKeyboardProfile? Find(string? appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return null;

            return BuiltIn.FirstOrDefault(p =>
                string.Equals(p.AppId, appId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>What a picker should call this app.</summary>
        public static string LabelFor(string appId) => Find(appId)?.Label ?? appId;

        /// <summary>
        /// Resolves the profile for <paramref name="appId"/>: a user profile for that app first (they are
        /// the one who measured their set), then a built-in, else null.
        /// </summary>
        public static SamsungRemoteKeyboardProfile? Resolve(
            string? appId,
            IEnumerable<KeyValuePair<string, string>>? userProfiles = null)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return null;

            foreach (var stored in userProfiles ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!string.Equals(stored.Key, appId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (SamsungRemoteKeyboardJson.TryParse(stored.Value, out var parsed, out _) && parsed is not null)
                    return parsed;
            }

            return Find(appId);
        }

        private static IReadOnlyList<SamsungRemoteKeyboardProfile> LoadBuiltIn()
        {
            var assembly = typeof(SamsungRemoteKeyboardProfiles).Assembly;
            var profiles = new List<SamsungRemoteKeyboardProfile>();

            foreach (var resource in assembly.GetManifestResourceNames()
                         .Where(n => n.Contains(ResourceMarker, StringComparison.Ordinal)
                                     && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                string json;
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream!))
                    json = reader.ReadToEnd();

                if (!SamsungRemoteKeyboardJson.TryParse(json, out var profile, out var error) || profile is null)
                {
                    // A shipped profile that does not parse is a build/packaging bug: fail loudly rather
                    // than shipping a remote that types nothing for that app.
                    throw new InvalidOperationException($"{resource}: {error}");
                }

                profiles.Add(profile);
            }

            if (profiles.Count == 0)
                throw new InvalidOperationException("no keyboard profiles were embedded (Remote/Keyboards/*.json)");

            return profiles;
        }
    }
}
