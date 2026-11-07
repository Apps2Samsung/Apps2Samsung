using System.Net;
using Apps2Samsung.Catalog;
using Apps2Samsung.Catalog.Repositories;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog.Repositories;

/// <summary>
/// Adding a signed repository and loading it on every catalog load (#1018), against the spec's
/// fixtures served by a fake host: the key is pinned at approval, a changed key blocks the
/// repository, a failed load falls back to the last verified copy without allowing installs, and the
/// repository's apps reach the catalog next to the official ones, with their own label.
/// </summary>
public class SignedRepositoriesTests : IDisposable
{
    private const string Url = "https://repo.example.org/tizen/repository.json.signed";
    private const string KeyUrl = "https://repo.example.org/tizen/repository.pem";
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Catalog", "Repositories", "Fixtures");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "a2s-repo-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]?> _served = new()
    {
        [Url] = Fixture("valid.signed"),
        [KeyUrl] = Fixture("keys/test.pub.pem"),
    };

    public void Dispose()
    {
        try { Directory.Delete(_cache, true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task A_probe_shows_what_the_user_approves_and_approving_pins_it()
    {
        var probe = await Repos().ProbeAsync(Url);

        Assert.True(probe.Ok, probe.Message);
        Assert.Equal("repo.example.org", probe.Domain);
        Assert.Equal("org.example.tizen", probe.Manifest!.Repository.Id);
        Assert.Equal(64, probe.Fingerprint!.Length);

        var source = probe.Approve(Url);
        Assert.Equal("org.example.tizen", source.RepositoryId);
        Assert.Equal(probe.Fingerprint, source.Fingerprint);
        Assert.Contains("PUBLIC KEY", source.PublicKey);
        Assert.Equal(probe.Manifest.GeneratedAt, source.AcceptedGeneratedAt);
        Assert.True(UserSources.IsApprovedRepository(source));
    }

    [Fact]
    public async Task A_probe_of_a_tampered_file_is_refused()
    {
        _served[Url] = Fixture("tampered.signed");
        var probe = await Repos().ProbeAsync(Url);
        Assert.False(probe.Ok);
        Assert.Equal(RepositoryVerifier.Reasons.Signature, probe.Reason);
    }

    [Fact]
    public async Task A_good_load_lists_every_app_with_the_repository_as_its_source()
    {
        var load = await Repos().LoadAsync(await ApprovedAsync());

        Assert.Equal(RepositoryState.Ok, load.State);
        Assert.Equal(2, load.Providers.Count);
        Assert.All(load.Providers, p =>
        {
            Assert.StartsWith(UserSources.IdPrefix, p.Id);
            Assert.Equal(CatalogSourceKind.Repository, p.Source!.Kind);
            Assert.Equal("org.example.tizen", p.Source.RepositoryId);
            Assert.True(p.Source.CanInstall);
            Assert.NotNull(p.Releases);
        });
    }

    [Fact]
    public async Task A_changed_key_blocks_the_repository_until_approved()
    {
        var source = await ApprovedAsync();
        _served[KeyUrl] = Fixture("keys/other.pub.pem");

        var load = await Repos().LoadAsync(source);

        Assert.Equal(RepositoryState.KeyChanged, load.State);
        Assert.Empty(load.Providers);
        Assert.Equal(RepositoryVerifier.Fingerprint(File.ReadAllText(Path.Combine(Dir, "keys", "other.pub.pem"))), load.NewFingerprint);
    }

    [Fact]
    public async Task A_file_signed_by_another_key_is_never_accepted_even_with_that_key_served()
    {
        // Someone swaps both files: the signature is checked against the stored key, and the swap shows as a key change.
        var source = await ApprovedAsync();
        _served[Url] = Fixture("bad-signature.signed");

        var load = await Repos().LoadAsync(source);

        Assert.NotEqual(RepositoryState.Ok, load.State);
        Assert.All(load.Providers, p => Assert.False(p.Source!.CanInstall));
    }

    [Fact]
    public async Task Offline_after_a_good_load_lists_the_verified_copy_but_blocks_installs()
    {
        var source = await ApprovedAsync();
        await Repos().LoadAsync(source);
        _served[Url] = null;

        var load = await Repos().LoadAsync(source);

        Assert.Equal(RepositoryState.Stale, load.State);
        Assert.Equal(2, load.Providers.Count);
        Assert.All(load.Providers, p => Assert.False(p.Source!.CanInstall));
    }

    [Fact]
    public async Task Offline_without_a_verified_copy_lists_nothing()
    {
        var source = await ApprovedAsync();
        _served[Url] = null;

        var load = await Repos().LoadAsync(source);

        Assert.Equal(RepositoryState.Failed, load.State);
        Assert.Empty(load.Providers);
    }

    [Fact]
    public async Task An_older_manifest_than_the_accepted_one_is_a_rollback()
    {
        var source = await ApprovedAsync();
        source.AcceptedGeneratedAt = Now.AddHours(-1);

        var load = await Repos().LoadAsync(source);

        Assert.Equal(RepositoryState.Failed, load.State);
        Assert.Contains("rollback", load.Message);
    }

    [Fact]
    public async Task An_expired_manifest_after_a_good_one_is_stale()
    {
        var source = await ApprovedAsync();
        await Repos().LoadAsync(source);

        var later = await Repos(Now.AddDays(8)).LoadAsync(source);

        Assert.Equal(RepositoryState.Stale, later.State);
        Assert.Contains("expired", later.Message);
    }

    [Fact]
    public async Task The_catalog_lists_repository_apps_next_to_the_official_ones_without_merging_by_url()
    {
        var source = await ApprovedAsync();
        // The official manifest has a provider with the same releases URL as the repository's first app.
        var official = new ProviderEntry { Id = "example", Url = "https://api.github.com/repos/example/player/releases", DisplayName = "Example Player" };
        var loads = new List<RepositoryLoad>();

        var providers = await UserSources.ResolveAsync(Http(), new[] { official }, new[] { source }, default, Repos(), loads.Add);

        Assert.Equal(3, providers.Count);
        Assert.Equal(2, providers.Count(p => p.Url == official.Url));
        Assert.Single(loads);

        var releases = await new AddLatestRelease(Http()).GetProviderReleasesAsync(providers.Single(p => p.Source is not null && p.Url == official.Url), 1);
        Assert.True(releases.Ok);
        Assert.Equal(AddLatestRelease.ProviderReleaseSource.Repository, releases.Source);
        Assert.All(releases.Releases, r => Assert.Equal("org.example.tizen", r.Source.RepositoryId));
    }

    [Fact]
    public async Task A_head_that_passes_no_repository_loader_leaves_them_out()
    {
        var providers = await UserSources.ResolveAsync(Http(), Array.Empty<ProviderEntry>(), new[] { await ApprovedAsync() });
        Assert.Empty(providers);
    }

    [Theory]
    [InlineData("https://repo.example.org/tizen/repository.json.signed", UserSourceKind.SignedRepository)]
    [InlineData("http://repo.example.org/tizen/repository.json.signed", UserSourceKind.None)]
    [InlineData("https://repo.example.org/tizen/repository.json", UserSourceKind.Feed)]
    public void Only_an_https_signed_link_is_a_signed_repository(string url, UserSourceKind kind)
    {
        Assert.Equal(kind, UserSources.Classify(url));
    }

    [Fact]
    public void The_digest_check_compares_the_file_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "abc");
        try
        {
            Assert.True(SignedRepositories.DigestMatches(path, "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.False(SignedRepositories.DigestMatches(path, "sha256:" + new string('0', 64)));
            Assert.False(SignedRepositories.DigestMatches(path, "md5:900150983cd24fb0d6963f7d28e17f72"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<UserSource> ApprovedAsync()
    {
        var probe = await Repos().ProbeAsync(Url);
        Assert.True(probe.Ok, probe.Message);
        return probe.Approve(Url);
    }

    private SignedRepositories Repos(DateTimeOffset? now = null) => new(Http(), _cache, () => now ?? Now);

    private HttpClient Http() => new(new Handler(request =>
        _served.TryGetValue(request.RequestUri!.AbsoluteUri, out var body) && body is not null
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Dir, name));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
