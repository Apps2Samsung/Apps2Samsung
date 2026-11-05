using System.Net;
using System.Security.Cryptography;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

public sealed class CatalogIconsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Directory.CreateTempSubdirectory("a2s-icons-").FullName, "catalog-icons");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7];
    private static readonly string PngHex = Convert.ToHexStringLower(SHA256.HashData(Png));
    private const string Url = "https://apps2samsung.com/catalog/icons/x.png";

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);

    /// <summary>Answers every request with the same bytes and counts them.</summary>
    private sealed class Stub(byte[]? body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) });
        }
    }

    private static Asset AssetWith(string? icon, string? digest) => new() { FileName = "a.wgt", Icon = icon, IconDigest = digest };

    [Fact]
    public async Task Download_WhenTheBytesHashToTheDigest_IsCachedUnderTheHash()
    {
        var stub = new Stub(Png);
        var icons = new CatalogIcons(new HttpClient(stub), _dir);
        var asset = AssetWith(Url, "sha256:" + PngHex.ToUpperInvariant());

        var path = await icons.PathForAsync(asset);

        Assert.Equal(Path.Combine(_dir, PngHex + ".png"), path);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path!));
        // The second ask is answered from memory, a fresh instance from the disk: no request either way.
        Assert.Equal(path, await icons.PathForAsync(asset));
        Assert.Equal(path, await new CatalogIcons(new HttpClient(stub), _dir).PathForAsync(asset));
        Assert.Equal(1, stub.Requests);
    }

    [Fact]
    public async Task WrongHash_NotAnImage_TooBig_AreNull()
    {
        Assert.Null(await new CatalogIcons(new HttpClient(new Stub(Png)), _dir).PathForAsync(AssetWith(Url, "sha256:" + new string('0', 64))));
        var svg = "<svg/>"u8.ToArray();
        Assert.Null(await new CatalogIcons(new HttpClient(new Stub(svg)), _dir).PathForAsync(AssetWith(Url, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(svg)))));
        var big = new byte[CatalogIcons.MaxBytes + 1];
        Png.CopyTo(big, 0);
        Assert.Null(await new CatalogIcons(new HttpClient(new Stub(big)), _dir).PathForAsync(AssetWith(Url, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(big)))));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("https://x/y.png", null)]
    [InlineData(null, "sha256:00")]
    [InlineData("https://x/y.png", "sha256:nope")]
    [InlineData("https://x/y.png", "md5:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ftp://x/y.png", "sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("y.png", "sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task MalformedFields_AreNoIcon_WithoutARequest(string? icon, string? digest)
    {
        var stub = new Stub(Png);
        var asset = AssetWith(icon, digest);

        Assert.Null(CatalogIcons.Hex(asset));
        Assert.Null(await new CatalogIcons(new HttpClient(stub), _dir).PathForAsync(asset));
        Assert.Equal(0, stub.Requests);
    }

    [Fact]
    public async Task DamagedCacheFile_IsReplaced()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, PngHex + ".png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var stub = new Stub(Png);

        var got = await new CatalogIcons(new HttpClient(stub), _dir).PathForAsync(AssetWith(Url, "sha256:" + PngHex));

        Assert.Equal(path, got);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, stub.Requests);
    }

    [Fact]
    public async Task FailedDownload_IsNotRetriedForAWhile()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var stub = new Stub(null, HttpStatusCode.BadGateway);
        var icons = new CatalogIcons(new HttpClient(stub), _dir, () => now);
        var asset = AssetWith(Url, "sha256:" + PngHex);

        Assert.Null(await icons.PathForAsync(asset));
        Assert.Null(await icons.PathForAsync(asset));
        Assert.Equal(1, stub.Requests);

        now += CatalogIcons.RetryAfter;
        Assert.Null(await icons.PathForAsync(asset));
        Assert.Equal(2, stub.Requests);
    }

    [Fact]
    public async Task ConcurrentAsks_ShareOneDownload()
    {
        var stub = new Stub(Png);
        var icons = new CatalogIcons(new HttpClient(stub), _dir);
        var asset = AssetWith(Url, "sha256:" + PngHex);

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => icons.PathForAsync(asset)));

        Assert.All(paths, p => Assert.Equal(Path.Combine(_dir, PngHex + ".png"), p));
        Assert.Equal(1, stub.Requests);
    }

    [Fact]
    public void IconAssetOf_PrefersTheDefaultBuild()
    {
        var digest = "sha256:" + PngHex;
        var release = new GitHubRelease
        {
            Assets =
            [
                new Asset { FileName = "Jellyfin-intros.wgt", Icon = "https://x/a.png", IconDigest = digest },
                new Asset { FileName = "Jellyfin.wgt", Icon = "https://x/b.png", IconDigest = digest },
                new Asset { FileName = "Jellyfin-plain.wgt" },
            ],
        };

        Assert.Equal("Jellyfin.wgt", CatalogIcons.IconAssetOf(release)?.FileName);
        release.Assets[1].Icon = null;
        Assert.Equal("Jellyfin-intros.wgt", CatalogIcons.IconAssetOf(release)?.FileName);
        Assert.Null(CatalogIcons.IconAssetOf(new GitHubRelease { Assets = [new Asset { FileName = "x.wgt" }] }));
        Assert.Null(CatalogIcons.IconAssetOf(null));
    }
}
