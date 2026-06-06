using Avalonia.Controls;
using Avalonia.Threading;
using Apps2Samsung.ViewModels;
using System;
using System.ComponentModel;

namespace Apps2Samsung.Views;

public partial class TvLogsWindow : Window
{
    private TvLogsViewModel? _viewModel;

    public TvLogsWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unhook old VM
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as TvLogsViewModel;

        // Hook new VM
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TvLogsViewModel.Logs))
        {
            Dispatcher.UIThread.Post(() =>
                LogScrollViewer.ScrollToEnd());
        }
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
