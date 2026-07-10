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

        // True when the Tizen debug/sdb port (26101) is reachable — i.e. the TV is ready to
        // install to. False for TVs detected only via the 8001 REST API (Developer Mode not
        // fully active yet); those are shown with a warning marker but are not installable.
        public bool DebugPortOpen { get; set; } = true;

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
