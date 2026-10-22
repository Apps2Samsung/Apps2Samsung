using CommunityToolkit.Mvvm.ComponentModel;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using System;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Settings for ImmiTV (the Immich TV client): the Immich server URL plus either an email +
    /// password or an API key. Persisted in <see cref="AppSettings"/> and written into the wgt's
    /// <c>js/config.js</c> (<c>IMMICH_DEFAULTS</c>) at install time by the shared ImmiTV package
    /// patcher, so the app's setup screen is pre-filled on the TV.
    /// </summary>
    public partial class ImmiTvSettingsViewModel : ViewModelBase, IDisposable
    {
        private readonly ILocalizationService _localizationService;

        [ObservableProperty] private string serverUrl = AppSettings.Default.ImmiTvServerUrl ?? string.Empty;
        [ObservableProperty] private string email = AppSettings.Default.ImmiTvEmail ?? string.Empty;
        [ObservableProperty] private string password = AppSettings.Default.ImmiTvPassword ?? string.Empty;
        [ObservableProperty] private string apiKey = AppSettings.Default.ImmiTvApiKey ?? string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PasswordChar))]
        private bool showSecrets;

        public char PasswordChar => ShowSecrets ? '\0' : '*';

        public string LblImmiTvSettings => _localizationService.GetString("lblImmiTvSettings");
        public string LblImmiTvHint => _localizationService.GetString("lblImmiTvHint");
        public string LblImmiTvOnlyNotice => _localizationService.GetString("lblImmiTvOnlyNotice");
        public string LblImmiTvServerUrl => _localizationService.GetString("lblImmiTvServerUrl");
        public string LblImmiTvEmail => _localizationService.GetString("lblImmiTvEmail");
        public string LblImmiTvPassword => _localizationService.GetString("lblImmiTvPassword");
        public string LblImmiTvApiKey => _localizationService.GetString("lblImmiTvApiKey");
        public string LblImmiTvApiKeyHint => _localizationService.GetString("lblImmiTvApiKeyHint");

        public ImmiTvSettingsViewModel(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
            _localizationService.LanguageChanged += OnLanguageChanged;
        }

        partial void OnServerUrlChanged(string value) => Save(s => s.ImmiTvServerUrl = value?.Trim() ?? string.Empty);
        partial void OnEmailChanged(string value) => Save(s => s.ImmiTvEmail = value?.Trim() ?? string.Empty);
        partial void OnPasswordChanged(string value) => Save(s => s.ImmiTvPassword = value ?? string.Empty);
        partial void OnApiKeyChanged(string value) => Save(s => s.ImmiTvApiKey = value?.Trim() ?? string.Empty);

        private static void Save(Action<AppSettings> apply)
        {
            apply(AppSettings.Default);
            AppSettings.Default.Save();
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(LblImmiTvSettings));
            OnPropertyChanged(nameof(LblImmiTvHint));
            OnPropertyChanged(nameof(LblImmiTvOnlyNotice));
            OnPropertyChanged(nameof(LblImmiTvServerUrl));
            OnPropertyChanged(nameof(LblImmiTvEmail));
            OnPropertyChanged(nameof(LblImmiTvPassword));
            OnPropertyChanged(nameof(LblImmiTvApiKey));
            OnPropertyChanged(nameof(LblImmiTvApiKeyHint));
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }
}
