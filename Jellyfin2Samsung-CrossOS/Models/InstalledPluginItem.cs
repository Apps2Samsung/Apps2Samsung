using CommunityToolkit.Mvvm.ComponentModel;

namespace Apps2Samsung.Models
{
    public partial class InstalledPluginItem : ObservableObject
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Version { get; init; }
        public bool IsSupported { get; init; }
        public bool HasVersion => !string.IsNullOrEmpty(Version);

        [ObservableProperty]
        private bool isSelected;
    }
}
