using CommunityToolkit.Mvvm.ComponentModel;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// One Camera PiP camera row (name + HLS video URL + MJPEG URL). Edited in the Camera PiP
    /// settings section and written into the widget's <c>config.js</c> at install time.
    /// </summary>
    public partial class CameraPipCameraEntry : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string videoUrl = string.Empty;

        [ObservableProperty]
        private string mjpegUrl = string.Empty;
    }
}
