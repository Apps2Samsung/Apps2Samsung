using Avalonia.Controls;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.Opened += async (_, __) =>
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.InitializeAsync();
                }
            };
        }
    }
}