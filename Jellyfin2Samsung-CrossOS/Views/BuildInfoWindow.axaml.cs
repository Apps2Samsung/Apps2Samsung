using Avalonia.Controls;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    public partial class BuildInfoWindow : Window
    {
        private readonly BuildInfoViewModel _vm = new();

        public BuildInfoWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            _vm.OnRequestClose += Close;
        }
    }
}
