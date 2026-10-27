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

        ViewModel = new IpInputDialogViewModel(loc, confirmed => Close());
        DataContext = ViewModel;
    }

    public async Task<string?> ShowDialogAsync(Window parent)
    {
        await ShowDialog(parent);
        return ViewModel.EnteredIp;
    }
}
