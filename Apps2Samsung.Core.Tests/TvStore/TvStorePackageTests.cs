using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.TvStore;

public class TvStorePackageTests
{
    private const string Tv = "192.168.8.195";

    // A `0 vd_applist` reply listing one app under the given Tizen id.
    private static string Listing(string tizenId) => $"""
        get app list...
        apps count: 1
        ---------------------------------------------------------------------------------------------
        --------------app_id                                =-------------
        --------------app_title                             =TV-Store-------------
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
