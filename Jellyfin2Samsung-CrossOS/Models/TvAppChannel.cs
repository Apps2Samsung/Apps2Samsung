using CommunityToolkit.Mvvm.ComponentModel;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// One TVApp channel entry (name + m3u8 stream URL). Edited in the TVApp settings
    /// section and written into the wgt's <c>js/main.js</c> channels array at install time.
    /// </summary>
    public partial class TvAppChannel : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string url = string.Empty;
    }
}
