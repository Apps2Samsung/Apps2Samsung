using Apps2Samsung.Certificate;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Localization;
using Apps2Samsung.Packaging;
using Apps2Samsung.Sdb;
using Apps2Samsung.Services;
using TizenSdb.SdbClient;

namespace Apps2Samsung.Cli;

/// <summary>The composition root: the Core services one run needs, built from the parsed options.</summary>
public sealed class CliServices : IAsyncDisposable
{
    public CliOptions Options { get; }
    public Output Out { get; }
    public CliAppConfig Config { get; }
    /// <summary>The desktop app's settings, shared through the data directory.</summary>
    public DesktopSettings Desktop { get; }
    public HttpClient Http { get; }
    public InProcessSdbEngine Sdb { get; }
    public INetworkService Network { get; }
    public CatalogLoader Catalog { get; }
    public CertificateProvisioningService Provisioning { get; }
    public PackageInstaller Installer { get; }
    /// <summary>English strings for the keys Core reports (guards, certificate steps).</summary>
    public LocalizationCatalog Strings { get; }

    public CliServices(CliOptions options)
    {
        Options = options;
        Out = new Output(options.Json, options.Quiet);
        CliPaths.Use(options.DataDir);
        Directory.CreateDirectory(CliPaths.CliDir);

        Desktop = new DesktopSettings(CliPaths.DataDir);
        Config = new CliAppConfig(Desktop)
        {
            GitHubToken = options.GitHubToken,
            DeletePreviousInstall = options.RemoveFirst,
            KeepWgtFile = options.Keep,
            ShowAllJellyfinVersions = options.AllVersions,
            ForceSamsungLogin = options.ForceLogin,
            ManualDuids = string.IsNullOrWhiteSpace(options.ManualDuids) ? Desktop.ManualDuids : options.ManualDuids,
            LocalIp = options.LocalIp ?? Desktop.LocalIp,
        };

        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Apps2Samsung-CLI");

        // The engine persists its RSA auth keypair; give it the CLI's own directory.
        SdbTcpDevice.KeyDirectory = CliPaths.CliDir;
        Sdb = new InProcessSdbEngine();
        // As on the desktop: a Tizen Studio sdb server (Device Manager, Emulator Manager) holds the
        // TV's one debug connection, so it is cleared before every fresh connect.
        InProcessSdbEngine.ClearCompetingSdbServer = ProcessHelper.KillSdbServers;

        // The desktop's extra scan IP is folded into the sweep here too.
        Network = new NetworkService(customIpProvider: () => Desktop.UserCustomIp, maxConcurrentHosts: 64);
        Catalog = new CatalogLoader(Http, options.GitHubToken);
        Strings = new LocalizationCatalog();

        var login = new BrowserSamsungLogin(!options.NoBrowser, TimeSpan.FromSeconds(options.LoginTimeoutSeconds), Out.Progress);
        var certService = new TizenCertificateService(Http, CertificateEndpoints.Default);
        Provisioning = new CertificateProvisioningService(certService, login);

        // Only the patcher that has input here: a community package's user_settings (#791).
        Installer = new PackageInstaller(Sdb, Config, new IPackagePatcher[] { new UserSettingsPatcher(Config) });
    }

    public async ValueTask DisposeAsync()
    {
        await Sdb.DisposeAsync();
        Http.Dispose();
    }
}
