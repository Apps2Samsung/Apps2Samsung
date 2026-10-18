using CommunityToolkit.Mvvm.ComponentModel;

namespace Apps2Samsung.ViewModels
{
    public partial class InstallingWindowViewModel : ObservableObject
    {
        private string _statusText = "Installing Tizen CLI...";

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }
        public void SetStatusText(string text)
        {
            StatusText = text;
        }

    }
}
