using Avalonia.Controls;
using Avalonia.Input;

namespace Apps2Samsung.Helpers
{
    /// <summary>Keyboard manners every sheet and dialog shares (#1124).</summary>
    public static class DialogKeys
    {
        /// <summary>
        /// Escape closes the window, the same way its title bar's X does, so whatever the window
        /// already does for a close (cancel a sign-in, hand back "nothing chosen") applies. A key a
        /// control used itself (an open drop-down, a flyout) arrives handled and is left alone.
        /// </summary>
        public static void CloseOnEscape(this Window window)
        {
            window.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || e.Handled || e.KeyModifiers != KeyModifiers.None)
                    return;
                e.Handled = true;
                window.Close();
            };
        }
    }
}
