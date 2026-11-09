using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.TvStore;

public class TvStorePackageTests
{
    private const string Tv = "192.168.8.195";

    // The id a set listed TV-Store under after an install under a new package id (#839).
    private const string RenamedAppId = "KUW3HHjMUR.Store";

    // A `0 vd_applist` reply listing one app under the given Tizen id.
    private static string Listing(string tizenId, string title = "TV-Store") => $"""
        get app list...
        apps count: 1
        ---------------------------------------------------------------------------------------------
        --------------app_id                                =-------------
        --------------app_title                             ={title}-------------
        --------------app_version                           =0.3.5-------------
        --------------app_tizen_id                          ={tizenId}-------------
        --------------app_size                              =1642498-------------
        --------------is_removable                          =1-------------
        ---------------------------------------------------------------------------------------------
        spend time for wascmd is [82]ms
        """;

    [Fact]
    public async Task Listed_IsInstalled()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing(TvStorePackage.AppId) });

        Assert.True(await TvStorePackage.IsInstalledAsync(sdb, Tv));
        Assert.Equal(TvStoreInstallState.Installed, await TvStorePackage.GetInstallStateAsync(sdb, Tv));
    }

    [Fact]
    public async Task ListingWithoutTvStore_IsNotInstalled()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing("org.tizen.netflix-app") });

        Assert.False(await TvStorePackage.IsInstalledAsync(sdb, Tv));
        Assert.Equal(TvStoreInstallState.NotInstalled, await TvStorePackage.GetInstallStateAsync(sdb, Tv));
        var found = await TvStorePackage.FindAsync(sdb, Tv);
        Assert.Equal(new TvStoreInstallation(TvStoreInstallState.NotInstalled, null), found);
        Assert.False(found.IsRenamed);
    }

    [Fact]
    public async Task Listed_FindsItUnderItsOwnId()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing(TvStorePackage.AppId) });

        var found = await TvStorePackage.FindAsync(sdb, Tv);

        Assert.Equal(new TvStoreInstallation(TvStoreInstallState.Installed, TvStorePackage.AppId), found);
        Assert.True(found.IsInstalled);
        Assert.False(found.IsRenamed);
    }

    // A TV-Store this device installed under a new package id (#839): the "On the TV" view already
    // showed it as TV-Store while Arm said "not installed". It is TV-Store, under the id the TV lists.
    [Fact]
    public async Task RenamedByThisDevice_IsInstalled_UnderTheListedId()
    {
        var renames = PackageIdRenameStore.Set(null, new PackageIdRenameRecord
        {
            TvDuid = "DUID",
            OriginalPackageId = TvStorePackage.PackageId,
            NewPackageId = "KUW3HHjMUR",
            OriginalAppId = TvStorePackage.AppId,
            NewAppId = RenamedAppId,
        });
        // The title the TV reports is irrelevant when the rename is on record.
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing(RenamedAppId, "Whatever") });

        var found = await TvStorePackage.FindAsync(sdb, Tv, renames);

        Assert.Equal(new TvStoreInstallation(TvStoreInstallState.Installed, RenamedAppId), found);
        Assert.True(found.IsRenamed);
        Assert.True(await TvStorePackage.IsInstalledAsync(sdb, Tv, renames));
        Assert.Equal(TvStoreInstallState.Installed, await TvStorePackage.GetInstallStateAsync(sdb, Tv, renames));
    }

    // A record from before the app ids were stored still maps by the package part of the id.
    [Fact]
    public async Task RenamedByThisDevice_PackageOnlyRecord_IsInstalled()
    {
        var renames = PackageIdRenameStore.Set(null, new PackageIdRenameRecord
        {
            TvDuid = "DUID",
            OriginalPackageId = TvStorePackage.PackageId,
            NewPackageId = "KUW3HHjMUR",
        });
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing(RenamedAppId, "Whatever") });

        Assert.Equal(RenamedAppId, (await TvStorePackage.FindAsync(sdb, Tv, renames)).AppId);
    }

    // Renamed by another device (the phone arms what the desktop installed): no record here, but a
    // rename rewrites the package part of the id only, and never the title.
    [Fact]
    public async Task RenamedByAnotherDevice_IsInstalled_ByNameAndTitle()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing(RenamedAppId, TvStorePackage.Title) });

        var found = await TvStorePackage.FindAsync(sdb, Tv);

        Assert.Equal(new TvStoreInstallation(TvStoreInstallState.Installed, RenamedAppId), found);
        Assert.True(found.IsRenamed);
    }

    [Theory]
    [InlineData("KUW3HHjMUR.Store", "Some Other Store")]   // the name, not the title
    [InlineData("KUW3HHjMUR.Shop", "Apps2Samsung TV-Store")] // the title, not the name
    [InlineData("KUW3HHjMUR", "Apps2Samsung TV-Store")]      // no application part at all
    public async Task LookAlike_WithoutARecord_IsNotInstalled(string tizenId, string title)
    {
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing(tizenId, title) });

        Assert.Equal(TvStoreInstallState.NotInstalled, await TvStorePackage.GetInstallStateAsync(sdb, Tv));
    }

    [Fact]
    public void Find_PrefersItsOwnId_OverARenamedCopy()
    {
        var apps = new[]
        {
            new InstalledApp(TvStorePackage.Title, null, RenamedAppId, "0.3.10", "", true),
            new InstalledApp(TvStorePackage.Title, null, TvStorePackage.AppId, "0.3.9", "", true),
        };

        Assert.Equal(TvStorePackage.AppId, TvStorePackage.Find(apps).AppId);
    }

    [Fact]
    public void Find_RenamedCopy_OverAnOutdatedOne()
    {
        var apps = new[]
        {
            new InstalledApp(TvStorePackage.Title, null, TvStorePackage.LegacyAppId, "0.3.0", "", true),
            new InstalledApp(TvStorePackage.Title, null, RenamedAppId, "0.3.10", "", true),
        };

        Assert.Equal(new TvStoreInstallation(TvStoreInstallState.Installed, RenamedAppId), TvStorePackage.Find(apps));
    }

    // The pre-0.3.5 id carries the same name and title; it is outdated, never a rename.
    [Fact]
    public void IsRenamed_LegacyId_IsFalse()
    {
        var legacy = new InstalledApp(TvStorePackage.Title, null, TvStorePackage.LegacyAppId, "0.3.0", "", true);

        Assert.False(TvStorePackage.IsRenamed(legacy, null));
    }

    [Fact]
    public async Task EmptyListing_IsNotInstalled()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { Output = "get app list...\napps count: 0\n" });

        Assert.False(await TvStorePackage.IsInstalledAsync(sdb, Tv));
    }

    [Fact]
    public async Task OnlyThePre035Id_IsOutdated()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { Output = Listing(TvStorePackage.LegacyAppId) });

        Assert.Equal(TvStoreInstallState.Outdated, await TvStorePackage.GetInstallStateAsync(sdb, Tv));
        Assert.False(await TvStorePackage.IsInstalledAsync(sdb, Tv));
        Assert.Equal(new TvStoreInstallation(TvStoreInstallState.Outdated, TvStorePackage.LegacyAppId), await TvStorePackage.FindAsync(sdb, Tv));
    }

    // #889: an S95D whose sdbd reset every connection read as "TV-Store is not installed".
    [Fact]
    public async Task ConnectionReset_Throws()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { ExitCode = 1, Error = "Connection reset by peer" });

        var ex = await Assert.ThrowsAsync<TvStoreUnreachableException>(() => TvStorePackage.IsInstalledAsync(sdb, Tv));
        Assert.Equal("Connection reset by peer", ex.Reason);
    }

    [Fact]
    public async Task FailedWithNoText_Throws()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { ExitCode = 1 });

        await Assert.ThrowsAsync<TvStoreUnreachableException>(() => TvStorePackage.GetInstallStateAsync(sdb, Tv));
    }

    [Fact]
    public async Task ConnectionReset_AttachThrowsUnreachable_NotNotInstalled()
    {
        var sdb = new AppsOnlySdb(new ProcessResult { ExitCode = 1, Error = "Remote closed stream while reading" });

        await Assert.ThrowsAsync<TvStoreUnreachableException>(() => TvStoreClient.AttachAsync(sdb, Tv));
    }

    // Answers the app listing and nothing else: the installed check must not touch any other verb.
    private sealed class AppsOnlySdb(ProcessResult apps) : ISdbEngine
    {
        public Task<ProcessResult> AppsAsync(string tvIpAddress) => Task.FromResult(apps);

        public Task<ProcessResult> DevicesAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DisconnectAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> CapabilityAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DuidAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> ExecuteAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass) => throw new NotSupportedException();
        public Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath, IProgress<int>? progress = null) => throw new NotSupportedException();
        public Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId) => throw new NotSupportedException();
        public Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath) => throw new NotSupportedException();
        public Task<ProcessResult> ShellAsync(string tvIpAddress, string command) => throw new NotSupportedException();
        public Task<IAsyncDisposable> ForwardAsync(string tvIpAddress, int localPort, int remotePort) => throw new NotSupportedException();
    }
}
