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
    /// <summary>Signed repositories (#1018): adding one; the catalog loads them through <see cref="Catalog"/>.</summary>
    public Apps2Samsung.Catalog.Repositories.SignedRepositories Repositories { get; }
    public CertificateProvisioningService Provisioning { get; }
    public PackageInstaller Installer { get; }
    /// <summary>English strings for the keys Core reports (guards, certificate steps).</summary>
    public LocalizationCatalog Strings { get; }

    // False when the engine is the menu's (see the constructor): the menu disposes it, not this run.
    private readonly bool _ownsSdb;

    /// <param name="sdb">The menu's engine, for a command the menu runs (#975). Null builds a new one.
    /// Samsung's sdbd serves one connection at a time on some sets (a 2020 UN32T4300 among them): a
    /// second engine next to the menu's opens a second connection while the menu's is still held, and
    /// the TV resets every attempt, so the install, uninstall or leftovers run from the menu failed with
    /// "Connection reset by peer" while the menu's own app list kept working. One engine, one connection.</param>
    public CliServices(CliOptions options, InProcessSdbEngine? sdb = null)
    {
        Options = options;
        Out = new Output(options);
        CliPaths.Use(options.DataDir);
        Directory.CreateDirectory(CliPaths.CliDir);
        // The session log (#975), except for `logs` itself, which would only list its own new file.
        if (options.Command is not ("logs" or "log"))
            CliLog.Start(Out, options);

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
        _ownsSdb = sdb is null;
        Sdb = sdb ?? new InProcessSdbEngine();
        // As on the desktop: a Tizen Studio sdb server (Device Manager, Emulator Manager) holds the
        // TV's one debug connection, so it is cleared before every fresh connect.
        InProcessSdbEngine.ClearCompetingSdbServer = ProcessHelper.KillSdbServers;

        // The desktop's extra scan IP is folded into the sweep here too.
        Network = new NetworkService(customIpProvider: () => Desktop.UserCustomIp, maxConcurrentHosts: 64);
        // The user's own sources: the ones stored with the desktop app, plus --source for this run.
        Catalog = new CatalogLoader(Http, options.GitHubToken, () =>
            Apps2Samsung.Catalog.UserSources.Parse(Desktop.UserSourcesJson)
                .Concat(options.Sources.Select(link => new Apps2Samsung.Catalog.UserSource { Url = link }))
                .ToList(), Out.Warn);
        Repositories = new Apps2Samsung.Catalog.Repositories.SignedRepositories(Http, Path.Combine(CliPaths.CliDir, "repositories"));
        Strings = new LocalizationCatalog();

        var login = new BrowserSamsungLogin(!options.NoBrowser, TimeSpan.FromSeconds(options.LoginTimeoutSeconds), Out.Progress);
        var certService = new TizenCertificateService(Http, CertificateEndpoints.Default);
        Provisioning = new CertificateProvisioningService(certService, login);

        // Only the patcher that has input here: a community package's user_settings (#791).
        Installer = new PackageInstaller(Sdb, Config, new IPackagePatcher[] { new UserSettingsPatcher(Config), new TvStoreAddressPatcher() });
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsSdb)
            await Sdb.DisposeAsync();
        Http.Dispose();
        Out.Dispose();
    }
}
