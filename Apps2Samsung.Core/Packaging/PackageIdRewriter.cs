using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// A completed package-id rename: the ids involved and every package file that changed
    /// (relative, '/'-separated paths). A rename that only touched <c>config.xml</c> and one that
    /// also had to rewrite app code are worth telling apart when an install still misbehaves.
    /// </summary>
    public sealed record PackageIdRename(string OldId, string NewId, IReadOnlyList<string> FilesChanged);

    /// <summary>
    /// Renames a Tizen package's id everywhere it occurs, not just in the
    /// <c>&lt;tizen:application&gt;</c> element.
    /// <para>
    /// Rewriting only that element is not enough and quietly produces a broken package. A package id
    /// also prefixes every service component id (<c>&lt;tizen:service id="Pkg.SomeService"&gt;</c>) —
    /// Tizen requires that prefix to match the owning package, so a half-renamed package installs its
    /// UI app while its services never register. Apps also launch their own service components by
    /// literal id from their JavaScript, which no manifest edit can reach. So the id is replaced
    /// across config.xml <b>and</b> the package's text files.
    /// </para>
    /// <para>
    /// The package is left unsigned-in-effect: any existing signatures are dropped, because the
    /// rewritten payload invalidates them. Callers must re-sign before installing — the install flow
    /// re-enters and re-signs, which is what they rely on.
    /// </para>
    /// </summary>
    public static class PackageIdRewriter
    {
        // Files whose bytes can meaningfully reference the package id. Binary assets (icons, fonts,
        // media) are skipped: a match there would be coincidence, and rewriting would corrupt them.
        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".xml", ".js", ".cjs", ".mjs", ".json", ".html", ".htm", ".css", ".txt",
        };

        // Bundled runtimes get large (Nuvio ships a 6 MB .cjs); read them, but refuse anything absurd
        // rather than pulling it into memory as a string.
        private const long MaxTextFileBytes = 32L * 1024 * 1024;

        /// <summary>Package ids are alphanumeric — the TV rejects anything else.</summary>
        public static bool IsValidPackageId(string? packageId) =>
            !string.IsNullOrEmpty(packageId) && Regex.IsMatch(packageId, "^[A-Za-z0-9]+$");

        /// <summary>
        /// Renames the package to a fresh random id of the same length. This is the escape hatch for a
        /// TV that refuses an install because the id collides with a copy installed under a different
        /// certificate — but prefer removing that copy, which keeps the package's own id and needs no
        /// rewriting at all. Returns null when there is no id to rewrite (nothing was changed).
        /// </summary>
        public static async Task<PackageIdRename?> RandomizeAsync(string wgtPath)
        {
            var oldPackageId = await WgtManifest.ReadPackageIdAsync(wgtPath);
            if (!IsValidPackageId(oldPackageId))
                return null;

            return await RenameAsync(wgtPath, RandomId(oldPackageId!.Length));
        }

        /// <summary>
        /// Renames the package to <paramref name="newPackageId"/>, replacing the current id in
        /// config.xml and in every text file that references it. Returns null when nothing was
        /// changed: no readable id, an invalid or identical target id, or a config.xml the id could
        /// not be rewritten in — in which case the package is left untouched.
        /// </summary>
        public static async Task<PackageIdRename?> RenameAsync(string wgtPath, string newPackageId)
        {
            if (string.IsNullOrEmpty(wgtPath) || !File.Exists(wgtPath))
                return null;

            if (!IsValidPackageId(newPackageId))
            {
                Trace.WriteLine($"[PackageId] Refusing rename to invalid package id '{newPackageId}'.");
                return null;
            }

            var oldPackageId = await WgtManifest.ReadPackageIdAsync(wgtPath);
            if (!IsValidPackageId(oldPackageId))
                return null;

            if (string.Equals(oldPackageId, newPackageId, StringComparison.Ordinal))
                return null;

            // Match the id only as a whole token, so an id that happens to be a prefix of a longer
            // one ("NuvioTV001" inside "NuvioTV0012") is left alone. A trailing '.' is not
            // alphanumeric, so "Pkg.SomeService" still matches on the "Pkg" part.
            var idPattern = new Regex(
                $@"(?<![A-Za-z0-9]){Regex.Escape(oldPackageId!)}(?![A-Za-z0-9])",
                RegexOptions.Compiled);

            using var ws = PackageWorkspace.Extract(wgtPath);
            var changed = new List<string>();

            foreach (var path in Directory.EnumerateFiles(ws.Root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(ws.Root, path).Replace(Path.DirectorySeparatorChar, '/');

                // The rewritten payload invalidates any signature over it; drop them so the package
                // can only be installed after the re-sign the caller is required to do.
                if (IsSignatureFile(relative))
                {
                    File.Delete(path);
                    continue;
                }

                if (!TextExtensions.Contains(Path.GetExtension(path)))
                    continue;

                var length = new FileInfo(path).Length;
                if (length == 0 || length > MaxTextFileBytes)
                    continue;

                if (await ReplaceInFileAsync(path, idPattern, newPackageId))
                    changed.Add(relative);
            }

            // config.xml is the one file the rename cannot skip: without it the package still declares
            // the old id and the retry would hit the identical conflict. Leave the package untouched
            // rather than repacking a no-op (or a partial rename of app code only).
            if (!changed.Contains("config.xml"))
            {
                Trace.WriteLine($"[PackageId] config.xml does not reference '{oldPackageId}'; leaving the package unchanged.");
                return null;
            }

            ws.Repack();

            Trace.WriteLine(
                $"[PackageId] Renamed {oldPackageId} -> {newPackageId} across {changed.Count} file(s): {string.Join(", ", changed)}");

            return new PackageIdRename(oldPackageId!, newPackageId, changed);
        }

        /// <summary>Replaces every whole-token match in one file. True if the file was rewritten.</summary>
        private static async Task<bool> ReplaceInFileAsync(string path, Regex idPattern, string newPackageId)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var bom = HasUtf8Bom(bytes) ? 3 : 0;
                var text = Encoding.UTF8.GetString(bytes, bom, bytes.Length - bom);

                var updated = idPattern.Replace(text, newPackageId);
                if (string.Equals(updated, text, StringComparison.Ordinal))
                    return false;

                // Round-trip the BOM: some packagers ship config.xml with one, and adding or dropping
                // it is a change we have no reason to make.
                await File.WriteAllTextAsync(path, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom > 0));
                return true;
            }
            catch (Exception ex)
            {
                // An unreadable file is not a reason to abandon the rename; the missing rewrite shows
                // up in the reported file list, and the install itself reports any real breakage.
                Trace.WriteLine($"[PackageId] Skipped {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        private static bool HasUtf8Bom(byte[] bytes) =>
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        private static bool IsSignatureFile(string relativePath) =>
            relativePath.Equals("author-signature.xml", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(relativePath, @"^signature[0-9]*\.xml$", RegexOptions.IgnoreCase);

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
