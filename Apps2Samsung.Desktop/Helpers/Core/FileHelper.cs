using Avalonia.Platform.Storage;
using Apps2Samsung.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public class FileHelper
    {
        private static readonly string[] wgtItem = ["*.wgt"];
        private static readonly string[] tpkItem = ["*.tpk"];
        private static readonly string[] allItem = ["*.wgt", "*.tpk"];
        private static readonly string[] imageItem = ["*.png"];

        /// <summary>
        /// Best-effort app title from a package file name/path (e.g. "Litefin-1.1.0.wgt" -> "Litefin").
        /// Used as the per-app key for custom icons and to identify the installed app.
        /// </summary>
        public static string AppTitleFromPackage(string fileNameOrPath)
            => Path.GetFileNameWithoutExtension(fileNameOrPath).Split('-')[0];

        public async Task<string?> BrowseImageFileAsync(IStorageProvider storageProvider)
        {
            var options = new FilePickerOpenOptions
            {
                Title = "Select a PNG icon",
                FileTypeFilter = new List<FilePickerFileType> { new("PNG images") { Patterns = imageItem } },
                AllowMultiple = false
            };

            var files = await storageProvider.OpenFilePickerAsync(options);
            return files?.FirstOrDefault()?.Path.LocalPath;
        }


public async Task<string?> BrowseWgtFilesAsync(IStorageProvider storageProvider)
{
    // The first entry is the picker's default-selected filter — keep the combined WGT/TPK
    // filter first so both package types are shown by default (was defaulting to WGT-only).
    var fileTypes = new List<FilePickerFileType>
    {
        new("WGT / TPK Files")
        {
            Patterns = allItem
        },
        new("WGT Files")
        {
            Patterns = wgtItem
        },
        new("TPK Files")
        {
            Patterns = tpkItem
        }
    };

    var options = new FilePickerOpenOptions
    {
        Title = "Select WGT/TPK File",
        FileTypeFilter = fileTypes,
        AllowMultiple = true
    };

    var files = await storageProvider.OpenFilePickerAsync(options);

    if (files?.Any() == true)
        return string.Join(";", files.Select(f => f.Path.LocalPath));

    return null;
}

        public List<ExtensionEntry> ParseExtensions(string output)
        {
            var extensions = new List<ExtensionEntry>();

            foreach (Match match in RegexPatterns.Extension.ExtensionEntry.Matches(output))
            {
                extensions.Add(new ExtensionEntry
                {
                    Index = int.Parse(match.Groups[1].Value),
                    Name = match.Groups[2].Value.Trim(),
                    Activated = bool.Parse(match.Groups[3].Value)
                });
            }

            return extensions;
        }
    }
}
