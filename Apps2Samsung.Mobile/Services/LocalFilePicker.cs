namespace Apps2Samsung.Mobile.Services;

/// <summary>A file the user picked, already copied into app cache so the platform handle can be released.</summary>
public sealed record PickedFile(string FileName, string LocalPath);

/// <summary>
/// The system file picker, returning a plain local path on every platform: the pages copy from
/// <see cref="PickedFile.LocalPath"/> and never see a content URI or a security-scoped URL.
/// <para>
/// <c>Platforms/Android</c> drives the Storage Access Framework itself rather than MAUI's
/// <c>FilePicker</c> (see that file for why); <c>Platforms/iOS</c> uses MAUI's picker over
/// <c>UIDocumentPickerViewController</c>. Both hand the picked stream to <see cref="CopyToCache"/>.
/// </para>
/// </summary>
public static partial class LocalFilePicker
{
    /// <summary>
    /// Shows the system picker and returns the chosen file, copied into app cache, or <c>null</c> if
    /// the user cancelled.
    /// </summary>
    /// <param name="mimeTypes">MIME filter, e.g. <c>image/*</c>. Empty means every file. iOS maps
    /// these to the matching uniform type identifiers.</param>
    public static Task<PickedFile?> PickAsync(params string[] mimeTypes) => PickPlatformAsync(mimeTypes);

    private static partial Task<PickedFile?> PickPlatformAsync(string[] mimeTypes);

    /// <summary>
    /// Copies the picked content into the app's "picked" cache folder under a sanitized file name.
    /// One file per pick: clearing first keeps a re-pick from reusing a stale copy and keeps the
    /// cache from growing with every icon the user tries. Callers that need the file to outlive the
    /// next pick copy it onwards (AppIconsPage does).
    /// </summary>
    internal static PickedFile CopyToCache(string? displayName, Stream source)
    {
        var name = Sanitize(displayName);

        var dir = Path.Combine(FileSystem.CacheDirectory, "picked");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var dest = Path.Combine(dir, name);
        using (var dst = File.Create(dest))
            source.CopyTo(dst);

        return new PickedFile(name, dest);
    }

    // The name comes from another app, so it decides neither the directory nor the extension.
    private static string Sanitize(string? name)
    {
        name = Path.GetFileName(name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return "picked-file";

        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 128 ? cleaned[^128..] : cleaned;
    }
}
