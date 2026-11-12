using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

/// <summary>The official catalog's schemaVersion (tizen-community-packages docs/catalog.md): a newer one is noticed, not refused.</summary>
public class CatalogSchemaTests : IDisposable
{
    public void Dispose() => CatalogSchema.Use(null);

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void Notices_only_a_newer_version(int version, bool newer)
    {
        CatalogSchema.Use(new CommunityCatalog { SchemaVersion = version });
        Assert.Equal(newer, CatalogSchema.NewerVersion is not null);
        Assert.Equal(newer, CatalogSchema.Notice(k => k == "lblCatalogSchemaNewer" ? "v{0}" : k) is not null);
        if (newer)
            Assert.Equal("v2", CatalogSchema.Notice(_ => "v{0}"));
    }

    [Fact]
    public void A_missing_catalog_clears_it()
    {
        CatalogSchema.Use(new CommunityCatalog { SchemaVersion = 3 });
        CatalogSchema.Use(null);
        Assert.Null(CatalogSchema.NewerVersion);
    }

    [Fact]
    public void Reads_the_field_from_json()
    {
        var catalog = System.Text.Json.JsonSerializer.Deserialize<CommunityCatalog>("""{ "schemaVersion": 2, "categories": {}, "apps": [] }""",
            Apps2Samsung.Helpers.Core.JsonSerializerOptionsProvider.Default);
        Assert.True(CatalogSchema.IsNewer(catalog));
    }
}
