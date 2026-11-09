using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.TvStore;

/// <summary>What Send sources to TV hands TV-Store (#1018, TV-Store#82): approved signed repositories only.</summary>
public class TvStoreSourcesPayloadTests
{
    private static UserSource Repo(string id) => new()
    {
        Url = $"https://{id}.example.org/repository.json.signed",
        Name = $"Repo {id}",
        RepositoryId = $"org.example.{id}",
        Fingerprint = "AB:CD" + new string('e', 60),
        PublicKey = "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----",
    };

    [Fact]
    public void Approved_repositories_go_with_their_pinned_id_fingerprint_and_key()
    {
        var json = TvStoreClient.SourcesPayload(new[] { Repo("one"), new UserSource { Url = "github.com/someone/app" }, Repo("two") });
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("https://one.example.org/repository.json.signed", items[0].GetProperty("url").GetString());
        Assert.Equal("org.example.one", items[0].GetProperty("repositoryId").GetString());
        Assert.Equal("abcd" + new string('e', 60), items[0].GetProperty("fingerprint").GetString());
        Assert.Equal("Repo one", items[0].GetProperty("name").GetString());
        Assert.Contains("PUBLIC KEY", items[0].GetProperty("publicKeyPem").GetString());
    }

    [Fact]
    public void Unsigned_sources_alone_are_refused()
    {
        Assert.Throws<ArgumentException>(() => TvStoreClient.SourcesPayload(new[] { new UserSource { Url = "github.com/someone/app" } }));
    }
}
