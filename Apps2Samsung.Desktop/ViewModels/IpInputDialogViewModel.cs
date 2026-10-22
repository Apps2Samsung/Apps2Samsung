using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Interfaces;
using System;

namespace Apps2Samsung.ViewModels
{
    public partial class IpInputDialogViewModel : ObservableObject
    {
        private readonly ILocalizationService _localization;
        private readonly Action<bool> _close;

        [ObservableProperty]
        private string? enteredIp;

        public string Title => _localization.GetString("IpWindowTitle");
        public string Message => _localization.GetString("IpWindowDescription");
        public string OkText => _localization.GetString("keyConfirm");
        public string CancelText => _localization.GetString("keyStop");
        public IRelayCommand OkCommand { get; }
        public IRelayCommand CancelCommand { get; }

        public IpInputDialogViewModel(ILocalizationService localization, Action<bool> close)
        {
            _localization = localization ?? throw new ArgumentNullException(nameof(localization));
            _close = close ?? throw new ArgumentNullException(nameof(close));

            OkCommand = new RelayCommand(OnOk);
            CancelCommand = new RelayCommand(OnCancel);
        }

        private void OnOk() => _close(true);

        private void OnCancel() => _close(false);
    }
}
