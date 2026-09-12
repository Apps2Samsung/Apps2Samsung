namespace Apps2Samsung.Remote
{
    /// <summary>
    /// The remote key codes Samsung's <c>samsung.remote.control</c> channel accepts. Only the set the
    /// remote UI offers is listed — the channel takes any <c>KEY_*</c> code, so a caller can pass one
    /// that isn't here (an unknown code is simply ignored by the TV).
    /// </summary>
    public static class SamsungRemoteKeys
    {
        // ---- Power ----
        /// <summary>Toggles standby. Only reaches a TV that is already awake: a sleeping set doesn't
        /// serve the remote API at all, so turning one on would need Wake-on-LAN (its MAC isn't part
        /// of the scan model today).</summary>
        public const string Power = "KEY_POWER";

        // ---- Navigation ----
        public const string Up = "KEY_UP";
        public const string Down = "KEY_DOWN";
        public const string Left = "KEY_LEFT";
        public const string Right = "KEY_RIGHT";
        public const string Enter = "KEY_ENTER";
        public const string Back = "KEY_RETURN";
        public const string Home = "KEY_HOME";
        public const string Menu = "KEY_MENU";
        /// <summary>The info banner. Not on the remote UI, but the first key of the service-menu
        /// sequence in <see cref="SamsungRemoteSequences"/>.</summary>
        public const string Info = "KEY_INFO";
        public const string Exit = "KEY_EXIT";
        public const string Source = "KEY_SOURCE";

        // ---- Volume ----
        public const string VolumeUp = "KEY_VOLUP";
        public const string VolumeDown = "KEY_VOLDOWN";
        public const string Mute = "KEY_MUTE";
        public const string ChannelUp = "KEY_CHUP";
        public const string ChannelDown = "KEY_CHDOWN";

        // ---- Playback ----
        public const string Play = "KEY_PLAY";
        public const string Pause = "KEY_PAUSE";
        /// <summary>Play/pause toggle. Older sets only implement the separate Play and Pause keys, so
        /// the remote UI sends this and falls back when the TV ignores it.</summary>
        public const string PlayPause = "KEY_PLAY_BACK";
        public const string Stop = "KEY_STOP";
        public const string Rewind = "KEY_REWIND";
        public const string FastForward = "KEY_FF";
        // Chapter/track skip. These trailing-underscore codes come from Samsung's key list and are
        // honoured by some sets only; a set that doesn't implement one ignores the press silently.
        public const string Previous = "KEY_REWIND_";
        public const string Next = "KEY_FF_";

        // ---- Digits (channel entry) ----
        public static string Digit(int digit) => $"KEY_{digit}";
    }
}
