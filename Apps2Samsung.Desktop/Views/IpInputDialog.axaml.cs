using Avalonia.Controls;
using Apps2Samsung.Interfaces;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace Apps2Samsung;

public partial class IpInputDialog : Window
{
    public IpInputDialogViewModel ViewModel { get; }

    public IpInputDialog()
    {
        InitializeComponent();

        var loc = App.Services.GetRequiredService<ILocalizationService>();

        ViewModel = new IpInputDialogViewModel(loc, confirmed =>
        {
            _confirmed = confirmed;
            Close();
        });
        DataContext = ViewModel;
        Opened += (_, _) => IpBox.Focus();
    }

    private bool _confirmed;

    /// <summary>The typed address, or null when the dialog was cancelled or closed (#1124: Cancel and
    /// the title bar's X used to hand back whatever was in the box, and the TV got added).</summary>
    public async Task<string?> ShowDialogAsync(Window parent)
    {
        await ShowDialog(parent);
        return _confirmed ? ViewModel.EnteredIp : null;
    }
}
