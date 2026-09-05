using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Apps2Samsung.Helpers
{
    /// <summary>
    /// The one window icon every desktop window uses (<c>Icon="{x:Static appicon:AppIcon.Current}"</c>).
    ///
    /// On Windows this is the multi-size <c>jelly2sams.ico</c> (16/20/24/32/40/48 as classic DIB frames,
    /// 64/128/256 as PNG). Avalonia's Win32 backend hands real ICO data to Windows, which then picks the
    /// frame drawn for each spot — 16px title bar, 32px taskbar, 256px Alt-Tab — instead of shrinking
    /// the 256px logo down to a dark smudge (the thin Tizen ring is under half a pixel at 16px). The
    /// small frames are rendered per size from the logo geometry with the ring thickened so it survives.
    ///
    /// Elsewhere the 256px PNG is used: X11 and macOS take a bitmap and scale it themselves, and the
    /// Linux packages ship the hicolor PNG the desktop entry refers to, so the two should match.
    /// </summary>
    public static class AppIcon
    {
        private static readonly Lazy<WindowIcon> Icon = new(Load);

        public static WindowIcon Current => Icon.Value;

        private static WindowIcon Load()
        {
            var asset = OperatingSystem.IsWindows()
                ? "avares://Apps2Samsung/Assets/jelly2sams.ico"
                : "avares://Apps2Samsung/Assets/jelly2sams.png";
            using var stream = AssetLoader.Open(new Uri(asset));
            return new WindowIcon(stream);
        }
    }
}
