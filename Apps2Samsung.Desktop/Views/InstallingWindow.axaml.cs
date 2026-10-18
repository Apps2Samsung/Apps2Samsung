using Avalonia.Controls;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung;

public partial class InstallingWindow : Window
{
    public InstallingWindowViewModel ViewModel { get; }

    public InstallingWindow()
    {
        InitializeComponent();

        ViewModel = new InstallingWindowViewModel();
        DataContext = ViewModel;
    }
}