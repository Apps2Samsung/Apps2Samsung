using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views;

public partial class TizenTubeSettingsView : UserControl
{
    public TizenTubeSettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        (DataContext as TizenTubeSettingsViewModel)?.RefreshWatermark();
    }
}
