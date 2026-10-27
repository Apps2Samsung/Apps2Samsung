using CommunityToolkit.Mvvm.ComponentModel;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Packaging;
using System;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Settings for TizenTube Cobalt: the opt-in rewrite of the Cobalt player's proxy address
    /// (127.0.0.2) to a LAN address, for the Tizen 9 "A network error has occurred" launch failure.
    /// Applied at install time by <see cref="TizenTubeProxyPatcher"/>.
    /// </summary>
    public partial class TizenTubeSettingsViewModel : ViewModelBase, IDisposable
    {
        private readonly ILocalizationService _localizationService;

        [ObservableProperty]
        private bool proxyOverride;

        [ObservableProperty]
        private string proxyHost;

        [ObservableProperty]
        private bool proxyHostInvalid;

        public string LblTizenTubeProxy => _localizationService.GetString("lblTizenTubeProxy");
        public string LblTizenTubeProxyOverride => _localizationService.GetString("lblTizenTubeProxyOverride");
        public string HintTizenTubeProxyOverride => _localizationService.GetString("hintTizenTubeProxyOverride");
        public string LblTizenTubeProxyHost => _localizationService.GetString("lblTizenTubeProxyHost");
        public string HintTizenTubeProxyHost => _localizationService.GetString("hintTizenTubeProxyHost");
        public string LblTizenTubeProxyHostInvalid => _localizationService.GetString("lblTizenTubeProxyHostInvalid");

        /// <summary>
        /// Shows what an empty field resolves to: the TV selected in the main window, which is the
        /// one the next install goes to.
        /// </summary>
        public string ProxyHostWatermark =>
            string.IsNullOrWhiteSpace(AppSettings.Default.TvIp)
                ? _localizationService.GetString("lblTizenTubeProxyHostAuto")
                : string.Format(_localizationService.GetString("lblTizenTubeProxyHostAutoIp"), AppSettings.Default.TvIp);

        public TizenTubeSettingsViewModel(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
            _localizationService.LanguageChanged += OnLanguageChanged;

            proxyOverride = AppSettings.Default.TizenTubeProxyOverride;
            proxyHost = AppSettings.Default.TizenTubeProxyHost;
        }

        partial void OnProxyOverrideChanged(bool value)
        {
            AppSettings.Default.TizenTubeProxyOverride = value;
            AppSettings.Default.Save();
        }

        // Empty means "the TV being installed to"; anything else must be a host[:port] or it isn't saved.
        partial void OnProxyHostChanged(string value)
        {
            var host = value?.Trim() ?? string.Empty;
            ProxyHostInvalid = host.Length > 0 && !TizenTubeProxyPatcher.TryParseHost(host, out _, out _);
            if (ProxyHostInvalid)
                return;

            AppSettings.Default.TizenTubeProxyHost = host;
            AppSettings.Default.Save();
        }

        /// <summary>The selected TV can change while this singleton lives; re-read it when shown.</summary>
        public void RefreshWatermark() => OnPropertyChanged(nameof(ProxyHostWatermark));

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(LblTizenTubeProxy));
            OnPropertyChanged(nameof(LblTizenTubeProxyOverride));
            OnPropertyChanged(nameof(HintTizenTubeProxyOverride));
            OnPropertyChanged(nameof(LblTizenTubeProxyHost));
            OnPropertyChanged(nameof(HintTizenTubeProxyHost));
            OnPropertyChanged(nameof(LblTizenTubeProxyHostInvalid));
            OnPropertyChanged(nameof(ProxyHostWatermark));
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }
}
