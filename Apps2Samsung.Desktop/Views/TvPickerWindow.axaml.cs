using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.Models;

namespace Apps2Samsung.Views
{
    /// <summary>What the TV sheet hands back: a TV, "enter an IP", or "rescan". Null = closed.</summary>
    public sealed record TvPickerResult(NetworkDevice? Device, bool Manual, bool Rescan);

    /// <summary>The TV sheet. Closes with a <see cref="TvPickerResult"/>.</summary>
    public partial class TvPickerWindow : Window
    {
        /// <param name="Detail">A few words on where the TV stands ("via relay", "points at 192.168.1.20"); empty for a ready direct TV.</param>
        public sealed record Row(NetworkDevice Device, string Name, string Subline, bool IsReady, bool IsSelected, string Detail)
        {
            public bool HasDetail => !string.IsNullOrEmpty(Detail);
        }

        public IReadOnlyList<Row> Rows { get; } = Array.Empty<Row>();
        public string TitleText { get; } = string.Empty;
        public string ManualText { get; } = string.Empty;
        public string ManualHint { get; } = string.Empty;
        public string RescanText { get; } = string.Empty;

        public TvPickerWindow()
        {
            InitializeComponent();
        }

        /// <param name="localIps">This machine's addresses, for the readiness verdict per row (the card's own, <see cref="Apps2Samsung.Design.TvCardStates.RowStatus"/>).</param>
        public TvPickerWindow(IEnumerable<NetworkDevice> devices, NetworkDevice? selected, Func<string, string> localize,
            IReadOnlyCollection<string>? localIps = null, string? localIp = null) : this()
        {
            Rows = devices.Select(d =>
            {
                var (led, detail) = Apps2Samsung.Design.TvCardStates.RowStatus(d, localIps ?? Array.Empty<string>(), localIp, localize);
                return new Row(
                    d,
                    string.IsNullOrWhiteSpace(d.DeviceName) ? localize("tvCardSamsungTv") : d.DeviceName!,
                    string.IsNullOrWhiteSpace(d.ModelName) ? d.IpAddress : $"{d.ModelName} · {d.IpAddress}",
                    led == Apps2Samsung.Design.TvLed.Ok,
                    selected is not null && selected.IpAddress == d.IpAddress,
                    detail);
            }).ToList();
            TitleText = localize("lblChooseTv");
            ManualText = localize("lblEnterIpManually");
            ManualHint = localize("lblEnterIpHint");
            RescanText = localize("lblRescan");
            DataContext = this;
        }

        private void OnRowClick(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is Row row)
                Close(new TvPickerResult(row.Device, false, false));
        }

        private void OnManualClick(object? sender, RoutedEventArgs e) => Close(new TvPickerResult(null, true, false));

        private void OnRescanClick(object? sender, RoutedEventArgs e) => Close(new TvPickerResult(null, false, true));
    }
}
