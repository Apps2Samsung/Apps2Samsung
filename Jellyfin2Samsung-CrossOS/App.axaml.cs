using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin;
using Apps2Samsung.Helpers.Jellyfin.Plugins;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Helpers.Tizen.Devices;
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
            ConfigureServices();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                DisableAvaloniaDataAnnotationValidation();

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
            services.AddSingleton<INetworkService, NetworkService>();
            services.AddSingleton<ITizenCertificateService, TizenCertificateService>();
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
            services.AddSingleton<JellyfinApiClient>();
            services.AddSingleton<TizenApiClient>();
            services.AddSingleton<PluginManager>();
            services.AddSingleton<JellyfinPackagePatcher>();

            // --------------------
            // Helpers
            // --------------------
            services.AddSingleton<DeviceHelper>();
            services.AddSingleton<PackageHelper>();
            services.AddSingleton<CertificateHelper>();
            services.AddSingleton<FileHelper>();
            services.AddSingleton<ProcessHelper>();
            services.AddSingleton<TvLogService>();

            // --------------------
            // ViewModels
            // --------------------
            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<InstallationCompleteViewModel>();
            services.AddTransient<InstallingWindowViewModel>();
            services.AddTransient<TvLogsViewModel>();
            services.AddSingleton<JellyfinConfigViewModel>();

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
                var vm = provider.GetRequiredService<JellyfinConfigViewModel>();
                return new JellyfinConfigView(vm);
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
    }
}
