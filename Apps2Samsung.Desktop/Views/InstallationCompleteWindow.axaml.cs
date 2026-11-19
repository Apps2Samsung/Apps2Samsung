using Avalonia.Controls;
using Apps2Samsung.ViewModels;
using System;

namespace Apps2Samsung;

public partial class InstallationCompleteWindow : Window
{
    public InstallationCompleteWindow(InstallationCompleteViewModel vm)
    {
        InitializeComponent();
        Apps2Samsung.Helpers.DialogKeys.CloseOnEscape(this);
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        Close();
    }
}