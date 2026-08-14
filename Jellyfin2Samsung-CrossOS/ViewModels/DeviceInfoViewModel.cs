using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Shows a TV's details (DUID, Tizen version, developer mode/IP, IP, …) gathered by the shared
    /// Core <see cref="Apps2Samsung.Sdb.TizenDeviceInfoService"/> — the same data the mobile head shows.
    /// </summary>
    public partial class DeviceInfoViewModel : ViewModelBase
    {
        private readonly ITizenInstallerService _installer;
        private readonly string _tvIp;
        private readonly bool _debugPortOpen;

        public string TvLabel { get; }

        public ObservableCollection<DeviceInfoRow> Rows { get; } = new();

        [ObservableProperty]
        private bool isBusy;

        [ObservableProperty]
        private string statusText = string.Empty;

        public event Action? OnRequestClose;

        public DeviceInfoViewModel(ITizenInstallerService installer, string tvIp, string tvLabel, bool debugPortOpen)
        {
            _installer = installer;
            _tvIp = tvIp;
            TvLabel = tvLabel;
            _debugPortOpen = debugPortOpen;
        }

        [RelayCommand]
        private async Task Load()
        {
            IsBusy = true;
            StatusText = "Reading TV information…";
            try
            {
                var info = await _installer.GetDeviceInfoAsync(_tvIp, _debugPortOpen);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Rows.Clear();
                    foreach (var row in info.Rows)
                        Rows.Add(row);
                });
                StatusText = TvLabel;
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't read TV information: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void Close() => OnRequestClose?.Invoke();
    }
}
