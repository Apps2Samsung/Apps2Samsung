using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// The form for a community package's <c>user_settings</c> (#791): one text box per field,
    /// the catalog's help page as a link, inline validation from <see cref="UserSettingsStore"/>.
    /// Closes with the values by key on Install, null on Cancel.
    /// </summary>
    public partial class UserSettingsWindow : Window
    {
        private sealed record Row(UserSettingsField Field, TextBox Box, TextBlock Error);

        private readonly List<Row> _rows = new();
        private readonly Func<string, string> _localize = key => key;
        private readonly string? _helpUrl;

        public string TitleText { get; } = string.Empty;
        public string HintText { get; } = string.Empty;
        public string HelpText { get; } = string.Empty;
        public string InstallText { get; } = string.Empty;
        public string CancelText { get; } = string.Empty;
        public bool HasHelp => !string.IsNullOrWhiteSpace(_helpUrl);

        public UserSettingsWindow()
        {
            InitializeComponent();
        }

        public UserSettingsWindow(string appName, UserSettingsSpec spec, IReadOnlyDictionary<string, string> current, Func<string, string> localize) : this()
        {
            _localize = localize;
            _helpUrl = spec.HelpUrl;
            TitleText = string.Format(localize("lblAppNeedsDetails"), appName);
            HintText = localize("hintAppUserSettings");
            HelpText = localize("lblWhereToGetThese") + " ↗";
            InstallText = localize("lblInstall");
            CancelText = localize("lblCancel");
            DataContext = this;

            foreach (var field in spec.Fields)
            {
                var label = new TextBlock { Classes = { "fieldlabel" }, Text = field.Required ? field.Label : $"{field.Label} ({localize("lblOptional")})" };
                var box = new TextBox
                {
                    Classes = { "field" },
                    Text = current.TryGetValue(field.Key, out var value) ? value : string.Empty,
                    // What an empty optional field installs with.
                    Watermark = field.Default,
                };
                var error = new TextBlock { Classes = { "fielderror" }, IsVisible = false };
                var row = new Row(field, box, error);
                // The error goes as soon as the value is right again.
                box.TextChanged += (_, _) => { if (error.IsVisible) Validate(row); };
                _rows.Add(row);
                Fields.Children.Add(new StackPanel { Spacing = 5, Children = { label, box, error } });
            }

            Opened += (_, _) =>
            {
                if (_rows.Count > 0)
                    _rows[0].Box.Focus();
            };
        }

        private bool Validate(Row row)
        {
            var key = UserSettingsStore.Validate(row.Field, row.Box.Text);
            row.Error.Text = key is null ? string.Empty : UserSettingsStore.Message(row.Field, key, _localize);
            row.Error.IsVisible = key is not null;
            return key is null;
        }

        private void OnInstallClick(object? sender, RoutedEventArgs e)
        {
            var ok = true;
            Row? firstBad = null;
            foreach (var row in _rows)
            {
                if (!Validate(row))
                {
                    ok = false;
                    firstBad ??= row;
                }
            }
            if (!ok)
            {
                firstBad!.Box.Focus();
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
                values[row.Field.Key] = (row.Box.Text ?? string.Empty).Trim();
            Close(values);
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

        private void OnHelpClick(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_helpUrl))
                return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = _helpUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[UserSettings] Couldn't open the help page: {ex.Message}");
            }
        }
    }
}
