using Avalonia.Controls;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung;

public partial class JellyfinConfigView : Window
{
    public JellyfinConfigView(JellyfinConfigViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}