using CommunityToolkit.Mvvm.ComponentModel;
using Apps2Samsung.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// One entry in the Settings window's left-hand list: a display name plus the
    /// view model rendered in the right pane (resolved to a view by <c>ViewLocator</c>).
    /// The display name is resolved through a factory so it can be re-read live when
    /// the UI language changes.
    /// </summary>
    public sealed class SettingsSection : ObservableObject
    {
        private readonly Func<string> _displayNameFactory;

        public ViewModelBase Content { get; }
        public string DisplayName => _displayNameFactory();

        public SettingsSection(Func<string> displayNameFactory, ViewModelBase content)
        {
            _displayNameFactory = displayNameFactory;
            Content = content;
        }

        /// <summary>Re-evaluates <see cref="DisplayName"/> (e.g. after a language change).</summary>
        public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>
    /// Host for the Settings window. Shows the generic "Application" settings first,
    /// followed by one section per registered <see cref="IAppSettingsProvider"/>
    /// (Jellyfin today, more apps later).
    /// </summary>
    public partial class SettingsWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly ILocalizationService _localizationService;

        public AppSettingsViewModel Application { get; }

        public ObservableCollection<SettingsSection> Sections { get; }

        [ObservableProperty]
        private SettingsSection? selectedSection;

        public SettingsWindowViewModel(
            AppSettingsViewModel application,
            IEnumerable<IAppSettingsProvider> providers,
            ILocalizationService localizationService)
        {
            Application = application;
            _localizationService = localizationService;

            Sections = new ObservableCollection<SettingsSection>
            {
                new SettingsSection(() => Application.LblTabMainSettings, application)
            };

            foreach (var provider in providers.OrderBy(p => p.SortOrder))
                Sections.Add(new SettingsSection(() => provider.DisplayName, provider.CreateSettingsViewModel()));

            SelectedSection = Sections.FirstOrDefault();

            _localizationService.LanguageChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            foreach (var section in Sections)
                section.RefreshDisplayName();
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }
}
