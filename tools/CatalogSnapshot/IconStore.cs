// The icon files published next to resolved.json for TV-Store (#1000, TV-Store#66):
// docs/catalog/icons/<sha256 of the bytes>.<png|jpg|gif|webp>, served at
// https://apps2samsung.com/catalog/icons/. The hash is the name, so the 200 Jellyfin builds that
// ship the same icon share one file, a URL never changes meaning and can be cached for good, and
// TV-Store's check (the bytes must hash to the asset's icon_digest) is the same rule as the name.
// After a run, Prune deletes the files no asset in the written snapshot refers to any more, so
// the folder does not grow with every release.

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Apps2Samsung.Packaging;

internal sealed class IconStore
{
    private static readonly Regex IconFileName = new(@"^[0-9a-f]{64}\.(png|jpg|gif|webp)$", RegexOptions.Compiled);

    private readonly string _directory;
    private readonly string _urlBase;
    private readonly object _writes = new();

    public IconStore(string directory, string urlBase)
    {
        _directory = directory;
        _urlBase = urlBase.EndsWith('/') ? urlBase : urlBase + "/";
    }

    public string Directory => _directory;

    /// <summary>
    /// Writes the icon under its hash (when it is not there yet) and returns the asset's two fields:
    /// the URL it is served at and <c>sha256:&lt;hex&gt;</c> of exactly those bytes.
    /// </summary>
    public (string Url, string Digest) Store(PackageIcon icon)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(icon.Bytes));
        var fileName = $"{hex}.{icon.Extension}";
        var path = Path.Combine(_directory, fileName);
        // Assets are read in parallel and the Jellyfin builds share one icon: one writer at a time.
        lock (_writes)
        {
            if (!File.Exists(path))
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.WriteAllBytes(path, icon.Bytes);
            }
        }
        return (_urlBase + fileName, "sha256:" + hex);
    }

    /// <summary>
    /// True when the file an asset's <c>icon</c> URL points at is in the folder, so the asset can
    /// keep the fields it had. False for a URL from elsewhere or a file that went missing: the asset
    /// is read again and the icon written anew.
    /// </summary>
    public bool Has(string? url)
    {
        var fileName = FileNameOf(url);
        return fileName is not null && File.Exists(Path.Combine(_directory, fileName));
    }

    /// <summary>
    /// Deletes every icon file none of <paramref name="referencedUrls"/> names. Only files shaped
    /// like an icon of this store are touched. Returns how many were deleted.
    /// </summary>
    public int Prune(IEnumerable<string?> referencedUrls)
    {
        if (!System.IO.Directory.Exists(_directory))
            return 0;

        var keep = new HashSet<string>(referencedUrls.Select(FileNameOf).OfType<string>(), StringComparer.Ordinal);
        var deleted = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(path);
            if (!IconFileName.IsMatch(name) || keep.Contains(name))
                continue;
            File.Delete(path);
            deleted++;
        }
        return deleted;
    }

    /// <summary>The file name an icon URL of this store ends in, null for any other URL.</summary>
    private string? FileNameOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith(_urlBase, StringComparison.Ordinal))
            return null;
        var name = url.Substring(_urlBase.Length);
        return IconFileName.IsMatch(name) ? name : null;
    }
}
