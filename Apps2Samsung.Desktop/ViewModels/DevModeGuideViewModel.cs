using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Design;
using Apps2Samsung.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apps2Samsung.ViewModels
{
    /// <summary>One numbered step of the Developer Mode guide window.</summary>
    public partial class DevModeGuideStepItem : ObservableObject
    {
        private readonly Func<string, Task> _copy;
        private readonly Func<string, string> _localize;

        public DevModeGuideStepItem(DevModeGuideStep step, bool ready, string? localIp, Func<string, string> localize, Func<string, Task> copy)
        {
            _copy = copy;
            _localize = localize;
            Number = step.Number;
            Title = localize(step.TitleKey);
            Hint = localize(step.HintKey);
            Needed = step.Needed;
            // Steps this TV no longer needs stay readable but step back; once ready none stand out.
            Opacity = step.Needed || ready ? 1 : 0.45;
            ShowsIp = step.ShowsLocalIp;
            HasIp = !string.IsNullOrEmpty(localIp);
            Ip = localIp ?? localize("devGuideNoLocalIp");
            copyText = localize("lblCopy");
        }

        public int Number { get; }
        public bool IsFirst => Number == 1;
        public string Title { get; }
        public string Hint { get; }
        public bool Needed { get; }
        public double Opacity { get; }
        public bool ShowsIp { get; }
        public bool HasIp { get; }
        public string Ip { get; }

        [ObservableProperty]
        private string copyText;

        [RelayCommand]
        private async Task CopyAsync()
        {
            await _copy(Ip);
            CopyText = _localize("lblCopied");
        }
    }

    /// <summary>
    /// The Developer Mode guide (Core <see cref="DevModeGuide"/>) for one TV, re-read every few
    /// seconds by <see cref="Views.DevModeGuideWindow"/> through <see cref="CheckAsync"/>.
    /// <see cref="Finished"/> fires with true to go on with the install, false to stop.
    /// </summary>
    public partial class DevModeGuideViewModel : ObservableObject
    {
        private readonly NetworkDevice _device;
        private readonly Func<Task> _refresh;
        private readonly IReadOnlyCollection<string> _localIps;
        private readonly string? _localIp;
        private readonly string _tvName;
        private readonly Func<string, string> _localize;
        private readonly Func<string, Task> _copy;

        public DevModeGuideViewModel(
            NetworkDevice device,
            string tvName,
            Func<Task> refresh,
            IReadOnlyCollection<string> localIps,
            string? localIp,
            bool fromInstall,
            Func<string, string> localize,
            Func<string, Task> copy)
        {
            _device = device;
            _tvName = tvName;
            _refresh = refresh;
            _localIps = localIps;
            _localIp = localIp;
            FromInstall = fromInstall;
            _localize = localize;
            _copy = copy;
            AnywayText = localize("devGuideInstallAnyway");
            AutoCheckText = localize("devGuideAutoCheck");
            Render();
        }

        public event Action<bool>? Finished;

        public bool FromInstall { get; }
        public string AnywayText { get; }
        public string AutoCheckText { get; }
        public ObservableCollection<DevModeGuideStepItem> Steps { get; } = new();

        [ObservableProperty] private string titleText = string.Empty;
        [ObservableProperty] private string subText = string.Empty;
        [ObservableProperty] private string statusText = string.Empty;
        [ObservableProperty] private string primaryText = string.Empty;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowAnyway))] private bool isReady;
        [ObservableProperty] private bool isChecking;

        public bool ShowAnyway => FromInstall && !IsReady;

        /// <summary>Re-reads the TV and redraws. <paramref name="showProgress"/> for the button.</summary>
        public async Task CheckAsync(bool showProgress)
        {
            if (IsChecking)
                return;
            IsChecking = true;
            if (showProgress)
                PrimaryText = _localize("devGuideChecking");
            try
            {
                await _refresh();
            }
            finally
            {
                IsChecking = false;
                Render();
            }
        }

        private void Render()
        {
            var problem = DevModeGuide.Diagnose(_device, _localIps);
            IsReady = problem == DevModeProblem.None;
            TitleText = _localize(DevModeGuide.TitleKey(problem));
            SubText = $"{_tvName} · {_device.IpAddress}";
            StatusText = DevModeGuide.Status(problem, _device, _localIp, _localize);
            PrimaryText = _localize(IsReady ? (FromInstall ? "devGuideContinueInstall" : "btn_Close") : "devGuideCheckAgain");

            Steps.Clear();
            foreach (var step in DevModeGuide.Steps(problem))
                Steps.Add(new DevModeGuideStepItem(step, IsReady, _localIp, _localize, _copy));
        }

        [RelayCommand]
        private async Task PrimaryAsync()
        {
            if (!IsReady)
            {
                await CheckAsync(showProgress: true);
                return;
            }
            Finished?.Invoke(FromInstall);
        }

        [RelayCommand]
        private void Anyway() => Finished?.Invoke(true);
    }
}
