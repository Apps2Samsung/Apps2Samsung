using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.Catalog;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// Add a source of your own: a GitHub repository or release, or a direct .wgt/.tpk link,
    /// checked with <see cref="UserSources.Validate"/> against the sources already there. Closes
    /// with the new <see cref="UserSource"/>, or null when cancelled.
    /// </summary>
    public partial class UserSourceWindow : Window
    {
        public sealed record Choice(string Id, string Name);

        private readonly Func<string, string> _localize = key => key;
        private readonly IReadOnlyList<UserSource> _existing = Array.Empty<UserSource>();

        public string TitleText { get; } = string.Empty;
        public string HintText { get; } = string.Empty;
        public string UrlLabel { get; } = string.Empty;
        public string UrlHint { get; } = string.Empty;
        public string NameLabel { get; } = string.Empty;
        public string CategoryLabel { get; } = string.Empty;
        public string PartnerLabel { get; } = string.Empty;
        public string PartnerHint { get; } = string.Empty;
        public string AddText { get; } = string.Empty;
        public string CancelText { get; } = string.Empty;

        public UserSourceWindow()
        {
            InitializeComponent();
        }

        public UserSourceWindow(Func<string, string> localize, IReadOnlyList<UserSource> existing) : this()
        {
            _localize = localize;
            _existing = existing;
            TitleText = localize("lblAddSourceTitle");
            HintText = localize("hintAddSourceForm");
            UrlLabel = localize("lblUserSourceUrl");
            UrlHint = localize("hintUserSourceUrl");
            NameLabel = localize("lblUserSourceName") + $" ({localize("lblOptional")})";
            CategoryLabel = localize("lblUserSourceCategory");
            PartnerLabel = localize("lblUserSourcePartner");
            PartnerHint = localize("hintUserSourcePartner");
            AddText = localize("lblAdd");
            CancelText = localize("lblCancel");
            DataContext = this;

            CategoryBox.ItemsSource = AppCategories.Ordered.Select(id => new Choice(id, AppCategories.DisplayName(id, localize))).ToList();
            CategoryBox.SelectedIndex = AppCategories.Ordered.Count - 1;
            UrlBox.Watermark = "github.com/yourname/your-app";

            Opened += (_, _) => UrlBox.Focus();
        }

        private UserSource Read() => new()
        {
            Url = UrlBox.Text?.Trim() ?? string.Empty,
            Name = NameBox.Text?.Trim() ?? string.Empty,
            Category = (CategoryBox.SelectedItem as Choice)?.Id ?? AppCategories.Other,
            Partner = PartnerBox.IsChecked == true,
        };

        private void OnAddClick(object? sender, RoutedEventArgs e)
        {
            var source = Read();
            var errors = UserSources.Validate(source, _existing);
            Show(UrlError, UserSources.FieldUrl);
            Show(NameError, UserSources.FieldName);
            if (errors.Count > 0)
            {
                (errors.ContainsKey(UserSources.FieldUrl) ? UrlBox : NameBox).Focus();
                return;
            }
            Close(source);

            void Show(TextBlock error, string field)
            {
                var bad = errors.TryGetValue(field, out var key);
                error.Text = bad ? _localize(key!) : string.Empty;
                error.IsVisible = bad;
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
    }
}
