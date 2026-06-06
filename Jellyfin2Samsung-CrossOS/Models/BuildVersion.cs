using CommunityToolkit.Mvvm.ComponentModel;

namespace Apps2Samsung.Models
{
    public partial class BuildVersion : ObservableObject
    {
        [ObservableProperty] private string fileName = string.Empty;
        [ObservableProperty] private string description = string.Empty;
        [ObservableProperty] private string repoUrl = string.Empty;
    }
}
