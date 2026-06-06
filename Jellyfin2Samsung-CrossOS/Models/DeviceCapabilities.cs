namespace Apps2Samsung.Models
{
    internal class DeviceCapabilities
    {
        public bool SupportsDeveloperMode { get; set; }
        public string ApiVersion { get; set; } = string.Empty;
        public string TizenVersion { get; set; } = string.Empty;
        public int ModelYear { get; set; }
    }
}
