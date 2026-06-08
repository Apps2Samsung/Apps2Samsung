using Avalonia.Controls;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
