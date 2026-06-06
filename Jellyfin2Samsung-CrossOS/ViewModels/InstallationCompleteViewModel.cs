using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Interfaces;
using System;
using System.Diagnostics;


namespace Apps2Samsung.ViewModels
{
    public partial class InstallationCompleteViewModel : ObservableObject
    {
        private readonly ILocalizationService _localization;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SuccessMessage))]
        private string installedPackageName = string.Empty;
        public InstallationCompleteViewModel(ILocalizationService localization)
        {
            Trace.WriteLine(installedPackageName);
            _localization = localization;
        }

        public string Title => _localization.GetString("InstallationSuccessful");
        //public string SuccessMessage => _localization.GetString("InstallationSuccessfulOn");
        public string SuccessMessage => string.Format(_localization.GetString("InstallationSuccessfulOn"), InstalledPackageName);

        public string EasyRight => _localization.GetString("lbleasyRight");
        public string Validation => _localization.GetString("lblValidation");
        public string Close => _localization.GetString("btn_Close");

        [RelayCommand]
        private async void OpenKoFi()
        {
            try
            {
                var url = "https://ko-fi.com/patrickst";
                var psi = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to open URL: {ex}");
            }
            CloseWindow();
        }

        [RelayCommand]
        private void CloseWindow()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? RequestClose;
    }
}
