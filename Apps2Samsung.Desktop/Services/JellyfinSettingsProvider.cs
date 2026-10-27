using Apps2Samsung.Interfaces;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Registers Jellyfin's settings (server / playback / CSS) as a section in the
    /// Settings window. Adding another app means adding another
    /// <see cref="IAppSettingsProvider"/> alongside this one.
    /// </summary>
    public sealed class JellyfinSettingsProvider : IAppSettingsProvider
    {
        public string ProviderId => "jellyfin";
        public string DisplayName => "Jellyfin";
        public int SortOrder => 0;

        public ViewModelBase CreateSettingsViewModel()
            => App.Services.GetRequiredService<JellyfinSettingsViewModel>();
    }
}
