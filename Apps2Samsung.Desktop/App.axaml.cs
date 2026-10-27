using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Core.Plugins;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin;
using Apps2Samsung.Helpers.Jellyfin.Plugins;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Services;
using Apps2Samsung.ViewModels;
using Apps2Samsung.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Net.Http;

namespace Apps2Samsung
{
    public partial class App : Application
    {
        private IServiceProvider _serviceProvider;

        public static IServiceProvider Services { get; private set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // The design tokens (Core) as resources, before any window is built.
            Resources.MergedDictionaries.Add(Apps2Samsung.Styles.DesignTheme.Build());

            ConfigureServices();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                DisableAvaloniaDataAnnotationValidation();
                ForwardWheelToOpenPopups();

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                });
            }

            // Apply saved theme on startup
            var themeService = _serviceProvider.GetRequiredService<IThemeService>();
            themeService.ApplyTheme();

            base.OnFrameworkInitializationCompleted();
        }

        private void ConfigureServices()
        {
            var services = new ServiceCollection();

            var settings = AppSettings.Load();

            // --------------------
            // Core services
            // --------------------
            services.AddSingleton(settings);
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<ILocalizationService, LocalizationService>();
            services.AddSingleton<IMacVendorLookup, ArpMacVendorLookup>();
            // The TV-name lookup is SDB-backed and lives on the installer; expose it under the
            // Core-side abstraction so NetworkService needn't know the full installer interface.
            services.AddSingleton<ITvNameResolver>(sp =>
                (TizenInstallerService)sp.GetRequiredService<ITizenInstallerService>());
            services.AddSingleton<INetworkService>(sp => new NetworkService(
                sp.GetRequiredService<ITvNameResolver>(),
                sp.GetRequiredService<IMacVendorLookup>(),
                () => AppSettings.Default.UserCustomIP));
            services.AddSingleton<ITizenCertificateService>(sp => new TizenCertificateService(
                sp.GetRequiredService<HttpClient>(),
                new CertificateEndpoints(
                    AppSettings.Default.AuthorEndpoint_V3,
                    AppSettings.Default.DistributorsEndpoint_V1,
                    AppSettings.Default.DistributorsEndpoint_V3)));
            // SDB engine: TizenSdb.Core driven in-process, the same engine the mobile head runs.
            // Nothing to download or update at first launch, so a fresh install works offline (#549).
            services.AddSingleton<ISdbEngine, Apps2Samsung.Sdb.InProcessSdbEngine>();
            // A Tizen Studio sdb server on this PC holds a TV's (or the emulator's) single debug
            // connection; stop it before every fresh connect, not only at startup.
            Apps2Samsung.Sdb.InProcessSdbEngine.ClearCompetingSdbServer = ProcessHelper.KillSdbServers;
            services.AddSingleton<ITizenInstallerService, TizenInstallerService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IUpdaterService, UpdaterService>();
            services.AddSingleton<IUpdateDialogService, UpdateDialogService>();

            // HttpClient (configured ONCE, with GitHub auth if available)
            services.AddSingleton(sp =>
            {
                var appSettings = sp.GetRequiredService<AppSettings>();
                var token = Helpers.Core.GitHubAuthHandler.ResolveToken(appSettings);
                var handler = new Helpers.Core.GitHubAuthHandler(token);

                var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                client.DefaultRequestHeaders.UserAgent.ParseAdd("SamsungJellyfinInstaller/1.1");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                return client;
            });

            services.AddSingleton<SamsungLoginService>();
            services.AddSingleton<ISamsungLoginService>(sp => sp.GetRequiredService<SamsungLoginService>());
            // Shared reuse-aware cert provisioning (single source of truth with the mobile head).
            services.AddSingleton<Apps2Samsung.Certificate.CertificateProvisioningService>();
            services.AddSingleton<JellyfinApiClient>();
            services.AddSingleton<PluginManager>();
            services.AddSingleton<JellyfinPackagePatcher>();

            // The shared settings contract + this head's bundled oblong-tile source, needed by the
            // Core CustomIconPackagePatcher.
            services.AddSingleton<Apps2Samsung.Configuration.IAppConfig>(_ => AppSettings.Default);
            services.AddSingleton<Apps2Samsung.Packaging.IOblongIconSource, DesktopOblongIconSource>();

            // Per-app package patchers (edit the .wgt before signing/install).
            services.AddSingleton<IPackagePatcher>(sp => sp.GetRequiredService<JellyfinPackagePatcher>());
            services.AddSingleton<IPackagePatcher, Apps2Samsung.Helpers.TvApp.TvAppPackagePatcher>();
            // ImmiTV: Immich server + login → js/config.js IMMICH_DEFAULTS at install (shared Core patcher).
            services.AddSingleton<IPackagePatcher, Apps2Samsung.Packaging.ImmiTvPackagePatcher>();
            services.AddSingleton<IPackagePatcher, Apps2Samsung.Packaging.TizenTubeProxyPatcher>();
            // Camera PiP: cameras → config.js window.PIP_CONFIG at install (shared Core patcher).
            services.AddSingleton<IPackagePatcher, Apps2Samsung.Packaging.CameraPipPackagePatcher>();
            // A community package's user_settings (API keys, server addresses asked for before the
            // install) → the file the catalog names, by exact-text replacement (#791).
            services.AddSingleton<IPackagePatcher, Apps2Samsung.Packaging.UserSettingsPatcher>();
            // Registered last so the user's chosen icon (custom PNG or the bundled oblong tile)
            // overrides the package's default and composes with the app-specific patchers above.
            // Now the shared Core patcher (was Apps2Samsung.Helpers.CustomIconPackagePatcher).
            services.AddSingleton<IPackagePatcher, Apps2Samsung.Packaging.CustomIconPackagePatcher>();
            // Custom app title → config.xml <name> at install (shared Core patcher).
            services.AddSingleton<IPackagePatcher, Apps2Samsung.Packaging.AppTitlePackagePatcher>();

            // Shared app catalog for the icon/title editor (manifest → entries; HasOblong via the
            // bundled-oblong source, so it stays correct per head).
            // The pre-resolved catalog from apps2samsung.com: one download per 10 minutes, shared by
            // every consumer, so the app list and the "?" view load with no api.github.com call.
            services.AddSingleton(sp => new Apps2Samsung.Catalog.ResolvedCatalogService(
                sp.GetRequiredService<System.Net.Http.HttpClient>(),
                System.IO.Path.Combine(ProviderManifestService.CacheDirectory, Apps2Samsung.Catalog.ResolvedCatalogService.CacheFileName)));
            // All-time download counts (the "Most downloaded" group), one download per hour.
            services.AddSingleton(sp => new Apps2Samsung.Catalog.DownloadStatsService(
                sp.GetRequiredService<System.Net.Http.HttpClient>(),
                System.IO.Path.Combine(ProviderManifestService.CacheDirectory, Apps2Samsung.Catalog.DownloadStatsService.CacheFileName)));
            services.AddSingleton<Apps2Samsung.Catalog.AppCatalog>(sp => new Apps2Samsung.Catalog.AppCatalog(
                new Apps2Samsung.Helpers.Core.AddLatestRelease(
                    sp.GetRequiredService<System.Net.Http.HttpClient>(),
                    snapshot: sp.GetRequiredService<Apps2Samsung.Catalog.ResolvedCatalogService>()),
                sp.GetRequiredService<Apps2Samsung.Packaging.IOblongIconSource>()));

            // --------------------
            // Helpers
            // --------------------
            services.AddSingleton<PackageHelper>();
            services.AddSingleton<CertificateHelper>();
            services.AddSingleton<FileHelper>();
            services.AddSingleton<TvLogService>();

            // --------------------
            // ViewModels
            // --------------------
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<InstallationCompleteViewModel>();
            services.AddTransient<InstallingWindowViewModel>();
            services.AddTransient<TvLogsViewModel>();
            services.AddSingleton<AppSettingsViewModel>();
            services.AddSingleton<AppIconsViewModel>();
            services.AddSingleton<JellyfinSettingsViewModel>();
            services.AddSingleton<TvAppSettingsViewModel>();
            services.AddSingleton<ImmiTvSettingsViewModel>();
            services.AddSingleton<TizenTubeSettingsViewModel>();
            services.AddSingleton<CameraPipSettingsViewModel>();
            services.AddSingleton<SettingsViewModel>();

            // App-specific settings sections (each app registers one provider).
            services.AddSingleton<IAppSettingsProvider, JellyfinSettingsProvider>();
            services.AddSingleton<IAppSettingsProvider, TvAppSettingsProvider>();
            services.AddSingleton<IAppSettingsProvider, ImmiTvSettingsProvider>();
            services.AddSingleton<IAppSettingsProvider, TizenTubeSettingsProvider>();
            services.AddSingleton<IAppSettingsProvider, CameraPipSettingsProvider>();

            // --------------------
            // Views
            // --------------------
            services.AddSingleton(provider =>
            {
                var vm = provider.GetRequiredService<MainWindowViewModel>();

                var window = new MainWindow
                {
                    DataContext = vm
                };

                // IMPORTANT: prevent memory leak
                window.Closed += (_, _) =>
                {
                    if (vm is IDisposable d)
                        d.Dispose();
                };

                return window;
            });

            services.AddTransient(provider =>
            {
                var vm = provider.GetRequiredService<InstallingWindowViewModel>();
                return new InstallingWindow
                {
                    DataContext = vm
                };
            });

            services.AddTransient(provider =>
            {
                var vm = provider.GetRequiredService<InstallationCompleteViewModel>();
                return new InstallationCompleteWindow(vm);
            });

            // --------------------
            // Build provider
            // --------------------
            _serviceProvider = services.BuildServiceProvider();
            Services = _serviceProvider;

            // Localization bootstrap
            var localizationService = _serviceProvider.GetRequiredService<ILocalizationService>();
            LocalizationExtensions.SetLocalizationService(localizationService);
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators
                    .OfType<DataAnnotationsValidationPlugin>()
                    .ToArray();

            foreach (var plugin in dataValidationPluginsToRemove)
                BindingPlugins.DataValidators.Remove(plugin);
        }

        // With Windows' "Scroll inactive windows" off, the wheel goes to the focused window
        // instead of an open dropdown, so pass it on (AvaloniaUI/Avalonia#16646).
        private static void ForwardWheelToOpenPopups()
        {
            InputElement.PointerWheelChangedEvent.AddClassHandler<LightDismissOverlayLayer>((layer, e) =>
            {
                var screen = layer.PointToScreen(e.GetPosition(layer));
                foreach (var popup in TopLevel.GetTopLevel(layer)!.GetVisualDescendants().OfType<Popup>())
                {
                    if (!popup.IsOpen || popup.Host is not Visual root)
                        continue;

                    var point = root.PointToClient(screen);
                    if ((root as IInputElement)?.InputHitTest(point) is Interactive target)
                    {
                        target.RaiseEvent(new PointerWheelEventArgs(target, e.Pointer, root, point,
                            e.Timestamp, e.Properties, e.KeyModifiers, e.Delta));
                        e.Handled = true;
                        return;
                    }
                }
            });
        }
    }
}
