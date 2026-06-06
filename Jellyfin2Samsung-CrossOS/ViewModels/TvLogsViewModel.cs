using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels;

public partial class TvLogsViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private readonly TvLogService _logService;

    [ObservableProperty]
    private string logs = string.Empty;

    [ObservableProperty]
    private TvLogConnectionStatus connectionStatus = TvLogConnectionStatus.Stopped;

    public string StatusText => ConnectionStatus switch
    {
        TvLogConnectionStatus.Stopped => "Stopped",
        TvLogConnectionStatus.Listening => "Listening",
        TvLogConnectionStatus.Connected => "Connected",
        TvLogConnectionStatus.NoConnections => "No connection",
        _ => ""
    };

    public bool CanStart => ConnectionStatus == TvLogConnectionStatus.Stopped;
    public bool CanStop => ConnectionStatus != TvLogConnectionStatus.Stopped;
    public bool IsListening => ConnectionStatus == TvLogConnectionStatus.Listening;
    public string EndpointText => $"{IpAddress}:54321";

    public string IpAddress { get; }
    public string StartLog => _localizationService.GetString("lblStartLog");
    public string StopLog => _localizationService.GetString("lblStopLog");
    public string SaveLogFile => _localizationService.GetString("lblSaveLogs");
    public string Close => _localizationService.GetString("btn_Close");

    public TvLogsViewModel(
        TvLogService logService,
        string ipAddress,
        ILocalizationService localizationService)
    {
        _localizationService = localizationService;
        _logService = logService;
        IpAddress = ipAddress;

        _localizationService.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StartLog));
            OnPropertyChanged(nameof(StopLog));
            OnPropertyChanged(nameof(SaveLogFile));
            OnPropertyChanged(nameof(Close));
        };
    }

    partial void OnConnectionStatusChanged(TvLogConnectionStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsListening));
    }

    [RelayCommand]
    private void Start()
    {
        Logs = "Waiting for TV connection...\n";
        ConnectionStatus = TvLogConnectionStatus.Listening;

        _logService.StartLogServer(
            54321,
            line => Dispatcher.UIThread.Post(() => Logs += line),
            status => Dispatcher.UIThread.Post(() => ConnectionStatus = status));
    }

    [RelayCommand]
    private void Stop()
    {
        _logService.Stop();
        ConnectionStatus = TvLogConnectionStatus.Stopped;
        Logs += "\n--- Log stream stopped ---\n";
    }

    [RelayCommand]
    private async Task SaveLogs()
    {
        var dialog = new Avalonia.Controls.SaveFileDialog
        {
            InitialFileName = $"tv_logs_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        var window = Avalonia.Application.Current?
            .ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (window == null)
            return;

        var result = await dialog.ShowAsync(window);
        if (!string.IsNullOrWhiteSpace(result))
            await File.WriteAllTextAsync(result, Logs);
    }
}
