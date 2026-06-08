using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Settings for the TVApp IPTV player: the user's channel list (name + m3u8 URL).
    /// Persisted as JSON in <see cref="AppSettings.TvAppChannelsJson"/> and written into
    /// the wgt's <c>js/main.js</c> at install time by the TVApp package patcher.
    /// </summary>
    public partial class TvAppSettingsViewModel : ViewModelBase, IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly ILocalizationService _localizationService;
        private bool _loading;

        public ObservableCollection<TvAppChannel> Channels { get; } = new();

        public bool HasChannels => Channels.Count > 0;

        public string LblTvAppSettings => _localizationService.GetString("lblTvAppSettings");
        public string LblTvAppHint => _localizationService.GetString("lblTvAppHint");
        public string LblTvAppOnlyNotice => _localizationService.GetString("lblTvAppOnlyNotice");
        public string LblChannelName => _localizationService.GetString("lblChannelName");
        public string LblChannelUrl => _localizationService.GetString("lblChannelUrl");
        public string LblAddChannel => _localizationService.GetString("lblAddChannel");
        public string LblNoChannels => _localizationService.GetString("lblNoChannels");

        public TvAppSettingsViewModel(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
            _localizationService.LanguageChanged += OnLanguageChanged;

            LoadChannels();
            Channels.CollectionChanged += OnChannelsChanged;
        }

        private void LoadChannels()
        {
            _loading = true;
            try
            {
                var json = AppSettings.Default.TvAppChannelsJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var items = JsonSerializer.Deserialize<List<TvAppChannel>>(json, JsonOptions);
                    if (items != null)
                    {
                        foreach (var channel in items)
                        {
                            channel.PropertyChanged += OnChannelPropertyChanged;
                            Channels.Add(channel);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TvAppSettings] Failed to load channels: {ex.Message}");
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // A Move keeps the same instance (it's in both Old/New items); only its
            // position changed, so just persist the new order without touching subscriptions.
            if (e.Action == NotifyCollectionChangedAction.Move)
            {
                Save();
                return;
            }

            if (e.NewItems != null)
                foreach (TvAppChannel channel in e.NewItems)
                    channel.PropertyChanged += OnChannelPropertyChanged;

            if (e.OldItems != null)
                foreach (TvAppChannel channel in e.OldItems)
                    channel.PropertyChanged -= OnChannelPropertyChanged;

            OnPropertyChanged(nameof(HasChannels));
            Save();
        }

        private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Save();

        private void Save()
        {
            if (_loading)
                return;

            AppSettings.Default.TvAppChannelsJson = JsonSerializer.Serialize(Channels);
            AppSettings.Default.Save();
        }

        [RelayCommand]
        private void AddChannel() => Channels.Add(new TvAppChannel());

        [RelayCommand]
        private void RemoveChannel(TvAppChannel? channel)
        {
            if (channel != null)
                Channels.Remove(channel);
        }

        // Channels play on the TV in this list's order, so let the user arrange it.
        [RelayCommand]
        private void MoveChannelUp(TvAppChannel? channel)
        {
            if (channel == null)
                return;

            var index = Channels.IndexOf(channel);
            if (index > 0)
                Channels.Move(index, index - 1);
        }

        [RelayCommand]
        private void MoveChannelDown(TvAppChannel? channel)
        {
            if (channel == null)
                return;

            var index = Channels.IndexOf(channel);
            if (index >= 0 && index < Channels.Count - 1)
                Channels.Move(index, index + 1);
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(LblTvAppSettings));
            OnPropertyChanged(nameof(LblTvAppHint));
            OnPropertyChanged(nameof(LblTvAppOnlyNotice));
            OnPropertyChanged(nameof(LblChannelName));
            OnPropertyChanged(nameof(LblChannelUrl));
            OnPropertyChanged(nameof(LblAddChannel));
            OnPropertyChanged(nameof(LblNoChannels));
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }
}
