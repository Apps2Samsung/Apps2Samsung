using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

public class TizenInstalledAppsTests
{
    // The shape of a real `0 vd_applist` reply (a Tizen 6.5 set), trimmed to the fields the parser reads.
    private const string Listing = """
        get app list...
        apps count: 3
        ---------------------------------------------------------------------------------------------
        --------------app_id                                =3201907018807-------------
        --------------app_title                             =Netflix-------------
        --------------app_version                           =70.23.15090-------------
        --------------install_date                          =2023-12-15 16:20:06-------------
        --------------app_tizen_id                          =org.tizen.netflix-app-------------
        --------------app_size                              =38658376-------------
        --------------is_removable                          =1-------------
        ---------------------------------------------------------------------------------------------
        ---------------------------------------------------------------------------------------------
        --------------app_id                                =111299001912-------------
        --------------app_title                             =YouTube-------------
        --------------app_version                           =2.1.513-------------
        --------------install_date                          =2025-05-04 14:03:32-------------
        --------------app_tizen_id                          =9Ur5IzDKqV.TizenYouTube-------------
        --------------app_size                              =73728-------------
        --------------is_removable                          =0-------------
        ---------------------------------------------------------------------------------------------
        ---------------------------------------------------------------------------------------------
        --------------app_id                                =-------------
        --------------app_title                             =-------------
        --------------app_version                           =1.0.0-------------
        --------------install_date                          =2026-10-26 10:55:40-------------
        --------------app_tizen_id                          =I2UuTnjfeL.AquaPlay-------------
        --------------app_size                              =1642498-------------
        --------------is_removable                          =1-------------
        ---------------------------------------------------------------------------------------------
        spend time for wascmd is [82]ms
        """;

    [Fact]
    public void Parse_ReadsEveryBlock_LargestFirst()
    {
        var apps = TizenInstalledApps.Parse(Listing);

        Assert.Equal(new[] { "org.tizen.netflix-app", "I2UuTnjfeL.AquaPlay", "9Ur5IzDKqV.TizenYouTube" }, apps.Select(a => a.TizenId));
        var netflix = apps[0];
        Assert.Equal("Netflix", netflix.Title);
        Assert.Equal("3201907018807", netflix.AppId);
        Assert.Equal("70.23.15090", netflix.Version);
        Assert.Equal("2023-12-15 16:20:06", netflix.InstallDate);
        Assert.Equal(38658376, netflix.SizeBytes);
        Assert.True(netflix.IsRemovable);
        Assert.False(apps.Single(a => a.Title == "YouTube").IsRemovable);
    }

    [Fact]
    public void Parse_UntitledApp_FallsBackToTheTizenId()
    {
        var app = TizenInstalledApps.Parse(Listing).Single(a => a.TizenId == "I2UuTnjfeL.AquaPlay");

        Assert.Equal(string.Empty, app.Title);
        Assert.Equal("I2UuTnjfeL.AquaPlay", app.DisplayName);
    }

    [Fact]
    public void Parse_WindowsLineEndings_ReadTheSame()
    {
        var apps = TizenInstalledApps.Parse(Listing.Replace("\n", "\r\n"));

        Assert.Equal(3, apps.Count);
    }

    [Fact]
    public void Parse_SameIdTwice_KeepsOne()
    {
        var block = Listing[Listing.IndexOf("--------------app_id", StringComparison.Ordinal)..];

        var apps = TizenInstalledApps.Parse(Listing + "\n" + block);

        Assert.Equal(3, apps.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("closed")]
    public void Parse_NoListing_IsEmpty(string? output)
    {
        Assert.Empty(TizenInstalledApps.Parse(output));
    }

    [Theory]
    [InlineData("get app list...\napps count: 0\n", true)]
    [InlineData("apps count: 00", true)]
    [InlineData("apps count: 10", false)]
    [InlineData("closed", false)]
    [InlineData(null, false)]
    public void IsEmptyListing(string? output, bool expected)
    {
        Assert.Equal(expected, TizenInstalledApps.IsEmptyListing(output));
    }

    [Fact]
    public void ParseOrThrow_EmptyListing_IsNoAppsNotAFailure()
    {
        var apps = TizenInstalledApps.ParseOrThrow(new ProcessResult { ExitCode = 0, Output = "get app list...\napps count: 0\n" });

        Assert.Empty(apps);
    }

    [Fact]
    public void ParseOrThrow_NoListing_ThrowsWithTheEngineText()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TizenInstalledApps.ParseOrThrow(new ProcessResult { ExitCode = 1, Error = "connection refused" }));

        Assert.Equal("connection refused", ex.Message);
    }

    [Fact]
    public void ParseOrThrow_NothingAtAll_SaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TizenInstalledApps.ParseOrThrow(null));

        Assert.Equal("The TV sent no app listing.", ex.Message);
    }
}
