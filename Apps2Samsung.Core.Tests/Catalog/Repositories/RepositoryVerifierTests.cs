using System.Text.Json;
using Apps2Samsung.Catalog.Repositories;

namespace Apps2Samsung.Tests.Catalog.Repositories;

/// <summary>
/// The shared test vectors of the independent-repository spec (#1018), copied from
/// tizen-community-packages <c>spec/fixtures</c> (index.json lists each case, its key, clock and
/// expected outcome). The keys there are public test keys; only the .pub.pem files are copied.
/// </summary>
public class RepositoryVerifierTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Catalog", "Repositories", "Fixtures");

    public static IEnumerable<object[]> Cases()
    {
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "index.json")));
        var i = 0;
        foreach (var c in index.RootElement.GetProperty("cases").EnumerateArray())
            yield return new object[] { i++, c.GetProperty("file").GetString()!, c.GetProperty("note").GetString()! };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_spec_vector_has_the_expected_outcome(int index, string file, string note)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "index.json")));
        var now = DateTimeOffset.Parse(doc.RootElement.GetProperty("now").GetString()!);
        var c = doc.RootElement.GetProperty("cases")[index];
        var key = File.ReadAllText(Path.Combine(Dir, c.GetProperty("key").GetString()!));
        DateTimeOffset? notBefore = c.TryGetProperty("notBefore", out var nb) ? DateTimeOffset.Parse(nb.GetString()!) : null;
        var fingerprint = c.TryGetProperty("fingerprint", out var fp) ? fp.GetString() : null;

        var result = RepositoryVerifier.Verify(File.ReadAllBytes(Path.Combine(Dir, file)), key, now, fingerprint, notBefore);

        if (c.GetProperty("expect").GetString() == "accept")
        {
            Assert.True(result.Ok, $"{file} ({note}): rejected [{result.Reason}] {result.Message}");
            Assert.NotNull(result.Manifest);
        }
        else
        {
            Assert.False(result.Ok, $"{file} ({note}): accepted");
            Assert.Equal(c.GetProperty("reason").GetString(), result.Reason);
        }
    }

    [Fact]
    public void The_valid_manifest_reads_into_the_model()
    {
        var result = Valid();

        var m = result.Manifest!;
        Assert.Equal("org.example.tizen", m.Repository.Id);
        Assert.False(string.IsNullOrWhiteSpace(m.Repository.Name));
        Assert.Equal(2, m.Providers.Count);
        var asset = m.Providers[0].Releases[0].Assets[0];
        Assert.StartsWith("sha256:", asset.Digest);
        Assert.StartsWith("https://", asset.DownloadUrl);
        Assert.Equal(64, result.Fingerprint!.Length);
    }

    [Fact]
    public void The_fingerprint_is_the_sha256_of_the_der_key()
    {
        var key = File.ReadAllText(Path.Combine(Dir, "keys", "test.pub.pem"));
        var fp = RepositoryVerifier.Fingerprint(key);

        Assert.Equal(Valid().Fingerprint, fp);
        Assert.True(Valid(fp).Ok);
        Assert.True(Valid(RepositoryVerifier.FormatFingerprint(fp!).ToUpperInvariant()).Ok);
    }

    [Fact]
    public void Another_repository_id_than_the_stored_one_is_rejected()
    {
        var result = Valid(expectedRepositoryId: "org.example.other");
        Assert.Equal(RepositoryVerifier.Reasons.RepositoryId, result.Reason);
    }

    [Fact]
    public void A_key_that_is_not_pem_is_a_key_error()
    {
        var file = File.ReadAllBytes(Path.Combine(Dir, "valid.signed"));
        var result = RepositoryVerifier.Verify(file, "not a key", DateTimeOffset.Parse("2026-11-07T12:00:00Z"));
        Assert.Equal(RepositoryVerifier.Reasons.Key, result.Reason);
    }

    private static RepositoryVerifier.Result Valid(string? fingerprint = null, string? expectedRepositoryId = null) =>
        RepositoryVerifier.Verify(
            File.ReadAllBytes(Path.Combine(Dir, "valid.signed")),
            File.ReadAllText(Path.Combine(Dir, "keys", "test.pub.pem")),
            DateTimeOffset.Parse("2026-11-07T12:00:00Z"),
            fingerprint, expectedRepositoryId: expectedRepositoryId);
}
