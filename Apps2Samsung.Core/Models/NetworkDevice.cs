namespace Apps2Samsung.Models
{
    public class NetworkDevice
    {
        public required string IpAddress { get; set; }
        public string? Manufacturer { get; set; }
        public string? DeviceName { get; set; }
        public string? ModelName { get; set; }
        public string? DeveloperMode { get; set; }
        public string? DeveloperIP { get; set; }
        // The TV's own MAC, as reported by /api/v2/ (device.wifiMac) while the set is awake. Kept on
        // the scan model because it is the only way to power a TV back ON: a sleeping set answers
        // nothing — no REST API, no remote channel — so the MAC has to be captured beforehand and
        // used for a Wake-on-LAN packet (#544).
        public string? MacAddress { get; set; }

        // True when the Tizen debug/sdb port (26101) is reachable — i.e. the TV is ready to
        // install to. False for TVs detected only via the 8001 REST API (Developer Mode not
        // fully active yet); those are shown with a warning marker but are not installable.
        public bool DebugPortOpen { get; set; } = true;

        // Whether TV-Store's sdb relay port (26201 by default) answered. Only probed when the TV's
        // Developer Mode points at 127.0.0.1, where the relay is the one way in (TV-Store#52);
        // null otherwise, or when nobody probed yet.
        public bool? RelayPortOpen { get; set; }

        // Why the relay is not listening, when TV-Store (0.3.15+) says: the bind error behind a switch
        // that is on but a port that does not answer. Null when it did not say, or there is no problem.
        public string? RelayError { get; set; }

        public string DisplayText
        {
            get
            {
                var prefix = DebugPortOpen ? string.Empty : "⚠ ";

                if (DeviceName is not null && ModelName is not null)
                    return $"{prefix}{IpAddress} | {ModelName} | {DeviceName}";

                if (DeviceName is not null)
                    return $"{prefix}{IpAddress} | {DeviceName}";

                if (Manufacturer is not null)
                    return $"{prefix}{IpAddress} | {Manufacturer}";

                return $"{prefix}{IpAddress}";
            }
        }
    }
    public class ExtensionEntry
    {
        public int Index;
        public string Name = "";
        public bool Activated;
    }
    public class NetworkInterfaceOption
    {
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;

        public string DisplayText => $"{Name} - {IpAddress}";
    }

}
