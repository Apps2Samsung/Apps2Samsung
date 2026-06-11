using Apps2Samsung.Interfaces;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Registers Litefin's settings (oblong icon) as a section in the Settings window.
    /// </summary>
    public sealed class LitefinSettingsProvider : IAppSettingsProvider
    {
        public string ProviderId => "litefin";
        public string DisplayName => "Litefin";
        public int SortOrder => 2;

        public ViewModelBase CreateSettingsViewModel()
            => App.Services.GetRequiredService<LitefinSettingsViewModel>();
    }
}
