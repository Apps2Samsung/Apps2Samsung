using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Packaging;

public class PackageIdRenameStoreTests
{
    private static PackageIdRenameRecord Rename(string duid, string original, string renamed, string? app = "App") => new()
    {
        TvDuid = duid,
        OriginalPackageId = original,
        NewPackageId = renamed,
        OriginalAppId = app is null ? null : $"{original}.{app}",
        NewAppId = app is null ? null : $"{renamed}.{app}",
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    public void Read_NothingUsable_IsEmpty(string? json)
    {
        Assert.Empty(PackageIdRenameStore.Read(json));
    }

    [Fact]
    public void Write_NoRecords_IsEmptyString()
    {
        Assert.Equal(string.Empty, PackageIdRenameStore.Write(new List<PackageIdRenameRecord>()));
    }

    [Fact]
    public void Set_ThenFind_RoundTrips()
    {
        var json = PackageIdRenameStore.Set(null, Rename("TV1", "AquaPlay02", "I2UuTnjfeL"));

        var found = PackageIdRenameStore.Find(json, "TV1", "AquaPlay02");

        Assert.NotNull(found);
        Assert.Equal("I2UuTnjfeL", found!.NewPackageId);
    }

    [Fact]
    public void Find_IsPerTv_AndTheDuidIgnoresCase()
    {
        var json = PackageIdRenameStore.Set(null, Rename("SHCLIM2Y3FAK6", "Pkg", "New"));

        Assert.NotNull(PackageIdRenameStore.Find(json, "shclim2y3fak6", "Pkg"));
        Assert.Null(PackageIdRenameStore.Find(json, "OTHERTV", "Pkg"));
        // Package ids are case-sensitive on Tizen.
        Assert.Null(PackageIdRenameStore.Find(json, "SHCLIM2Y3FAK6", "pkg"));
        Assert.Null(PackageIdRenameStore.Find(json, null, "Pkg"));
    }

    [Fact]
    public void Set_SameTvAndPackage_Replaces()
    {
        var json = PackageIdRenameStore.Set(null, Rename("TV1", "Pkg", "First"));
        json = PackageIdRenameStore.Set(json, Rename("TV1", "Pkg", "Second"));
        json = PackageIdRenameStore.Set(json, Rename("TV2", "Pkg", "Other"));

        var records = PackageIdRenameStore.Read(json);

        Assert.Equal(2, records.Count);
        Assert.Equal("Second", PackageIdRenameStore.Find(json, "TV1", "Pkg")!.NewPackageId);
    }

    [Fact]
    public void Remove_ForgetsOnlyThatTv()
    {
        var json = PackageIdRenameStore.Set(null, Rename("TV1", "Pkg", "A"));
        json = PackageIdRenameStore.Set(json, Rename("TV2", "Pkg", "B"));

        json = PackageIdRenameStore.Remove(json, "TV1", "Pkg");

        Assert.Null(PackageIdRenameStore.Find(json, "TV1", "Pkg"));
        Assert.NotNull(PackageIdRenameStore.Find(json, "TV2", "Pkg"));
    }

    [Fact]
    public void Remove_NoMatch_LeavesTheJsonAlone()
    {
        var json = PackageIdRenameStore.Set(null, Rename("TV1", "Pkg", "A"));

        Assert.Same(json, PackageIdRenameStore.Remove(json, "TV9", "Pkg"));
    }

    [Fact]
    public void OriginalAppIdOf_RenamedApp_GivesTheCatalogId()
    {
        var json = PackageIdRenameStore.Set(null, Rename("TV1", "AquaPlay02", "I2UuTnjfeL", "AquaPlay"));

        Assert.Equal("AquaPlay02.AquaPlay", PackageIdRenameStore.OriginalAppIdOf(json, "I2UuTnjfeL.AquaPlay"));
        Assert.Null(PackageIdRenameStore.OriginalAppIdOf(json, "Unrelated.App"));
    }

    [Fact]
    public void OriginalAppIdOf_RecordWithoutAppIds_MatchesOnThePackage()
    {
        var json = PackageIdRenameStore.Set(null, Rename("TV1", "Pkg", "New", app: null));

        Assert.Equal("Pkg.Main", PackageIdRenameStore.OriginalAppIdOf(json, "New.Main"));
    }

    [Fact]
    public void InstalledAppIdOf_MapsOnlyOnThatTv()
    {
        var json = PackageIdRenameStore.Set(null, Rename("TV1", "Pkg", "New", "Main"));

        Assert.Equal("New.Main", PackageIdRenameStore.InstalledAppIdOf(json, "TV1", "Pkg.Main"));
        Assert.Equal("Pkg.Main", PackageIdRenameStore.InstalledAppIdOf(json, "TV2", "Pkg.Main"));
    }
}
