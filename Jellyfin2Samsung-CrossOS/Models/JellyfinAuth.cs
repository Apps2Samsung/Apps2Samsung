namespace Apps2Samsung.Models
{
    public class JellyfinAuth
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class JellyfinUser
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        public override string ToString() => Name;
    }

    public class JellyfinPublicSystemInfo
    {
        public string? LocalAddress { get; set; }
        public string? ServerName { get; set; }
        public string? Version { get; set; }
        public string? ProductName { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Id { get; set; }
        public bool StartupWizardCompleted { get; set; }
    }
}
