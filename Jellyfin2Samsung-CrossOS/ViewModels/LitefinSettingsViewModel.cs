using CommunityToolkit.Mvvm.ComponentModel;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using System;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Settings for the Litefin (MoazSalem/litefin) Jellyfin client. Currently just the
    /// optional 16:9 "oblong" launcher icon for older Tizen 5.5 TVs; applied to the wgt at
    /// install time by the Litefin package patcher.
    /// </summary>
    public partial class LitefinSettingsViewModel : ViewModelBase, IDisposable
    {
        private readonly ILocalizationService _localizationService;

        [ObservableProperty]
        private bool useOblongIcon;

        public string LblLitefinSettings => _localizationService.GetString("lblLitefinSettings");
        public string LblLitefinOnlyNotice => _localizationService.GetString("lblLitefinOnlyNotice");
        public string LblOblongIcon => _localizationService.GetString("lblTvAppOblongIcon");
        public string LblOblongIconHint => _localizationService.GetString("lblTvAppOblongIconHint");

        public LitefinSettingsViewModel(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
            _localizationService.LanguageChanged += OnLanguageChanged;

            // Set the backing field directly so initialization doesn't trigger a save.
            useOblongIcon = AppSettings.Default.LitefinUseOblongIcon;
        }

        partial void OnUseOblongIconChanged(bool value)
        {
            AppSettings.Default.LitefinUseOblongIcon = value;
            AppSettings.Default.Save();
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(LblLitefinSettings));
            OnPropertyChanged(nameof(LblLitefinOnlyNotice));
            OnPropertyChanged(nameof(LblOblongIcon));
            OnPropertyChanged(nameof(LblOblongIconHint));
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }
}
