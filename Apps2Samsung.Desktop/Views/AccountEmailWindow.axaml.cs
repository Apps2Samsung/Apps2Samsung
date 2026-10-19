using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// Shown when the Samsung account has no email address, which the distributor certificate needs
    /// (issue #606). Only the user can fix it, on their account page, so it says how and opens it.
    /// </summary>
    public partial class AccountEmailWindow : Window
    {
        public sealed record FixStep(int Number, string Text)
        {
            public bool IsFirst => Number == 1;
        }

        public string TitleText { get; } = string.Empty;
        public string BodyText { get; } = string.Empty;
        public string OpenText { get; } = string.Empty;
        public string CloseText { get; } = string.Empty;
        public IReadOnlyList<FixStep> FixSteps { get; } = Array.Empty<FixStep>();

        public AccountEmailWindow()
        {
            InitializeComponent();
        }

        public AccountEmailWindow(Func<string, string> localize) : this()
        {
            TitleText = localize("lblSamsungAccountNoEmailTitle");
            BodyText = localize("certNoEmailBody");
            OpenText = localize("lblOpenSamsungAccount");
            CloseText = localize("btn_Close");
            FixSteps = new[] { "certNoEmailStep1", "certNoEmailStep2", "certNoEmailStep3" }
                .Select((key, i) => new FixStep(i + 1, localize(key)))
                .ToList();
            DataContext = this;
        }

        private void OnOpenClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = SamsungAccountEmailMissingException.AccountUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Cert] Couldn't open the Samsung account page: {ex.Message}");
            }
            Close();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
