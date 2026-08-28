using Apps2Samsung.Helpers.Core;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Rewrites a <c>.wgt</c>'s <c>config.xml</c> in place. Reading it is
    /// <see cref="WgtManifest"/>'s job; this is the mutating half, kept separate so the read path
    /// stays obviously side-effect free. A rewritten package must be re-signed before install —
    /// the install flow re-enters and re-signs, which is what callers rely on.
    /// </summary>
    public static class WgtConfigEditor
    {
        /// <summary>
        /// Replaces the package id with a fresh random one of the same length. This is the escape
        /// hatch for a TV that refuses an install because the id collides with something already
        /// installed under a different certificate. Returns false when there is no package id to
        /// rewrite (nothing was changed).
        /// </summary>
        public static async Task<bool> RandomizePackageIdAsync(string wgtPath)
        {
            if (string.IsNullOrEmpty(wgtPath) || !File.Exists(wgtPath))
                return false;

            var oldPackageId = await WgtManifest.ReadPackageIdAsync(wgtPath);
            if (string.IsNullOrEmpty(oldPackageId))
                return false;

            var newPackageId = RandomId(oldPackageId.Length);

            // Rewrite through a memory copy: a ZipArchive opened in Update mode over the file itself
            // would leave a truncated package behind if anything threw mid-write.
            using var buffer = new MemoryStream();
            using (var source = File.OpenRead(wgtPath))
                await source.CopyToAsync(buffer);

            buffer.Position = 0;

            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
            {
                var configEntry = archive.GetEntry("config.xml");
                if (configEntry is null)
                    return false;

                string config;
                using (var reader = new StreamReader(configEntry.Open(), Encoding.UTF8))
                    config = await reader.ReadToEndAsync();

                var pattern = RegexPatterns.WgtConfig.CreatePackageIdReplacePattern(oldPackageId);
                var updated = new Regex(pattern, RegexOptions.Multiline)
                    .Replace(config, match => match.Value.Replace(oldPackageId, newPackageId));

                if (updated == config)
                    return false;

                configEntry.Delete();
                var newEntry = archive.CreateEntry("config.xml");
                using var writer = new StreamWriter(newEntry.Open(), Encoding.UTF8);
                await writer.WriteAsync(updated);
            }

            await File.WriteAllBytesAsync(wgtPath, buffer.ToArray());
            return true;
        }

        private static string RandomId(int length)
        {
            var alphabet = Constants.CharacterSets.AlphaNumeric;
            var id = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                id.Append(alphabet[Random.Shared.Next(alphabet.Length)]);
            return id.ToString();
        }
    }
}
