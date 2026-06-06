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

        public string DisplayText
        {
            get
            {
                if (DeviceName is not null && ModelName is not null)
                    return $"{IpAddress} | {ModelName} | {DeviceName}";

                if (DeviceName is not null)
                    return $"{IpAddress} | {DeviceName}";

                if (Manufacturer is not null)
                    return $"{IpAddress} | {Manufacturer}";

                return IpAddress;
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
