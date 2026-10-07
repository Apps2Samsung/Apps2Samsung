using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Models
{
    public class ExistingCertificates
    {
        public required string Name { get; set; }
        // What the Settings dropdown shows; Name stays the stored identifier.
        [JsonIgnore]
        public string DisplayName { get => _displayName ?? Name; set => _displayName = value; }
        private string? _displayName;
        public required string Duid { get; set; }
        public string? File { get; set; }
        public string? Location { get; set; }
        public DateTime? ExpireDate { get; set; }
        public bool? Expired => ExpireDate.HasValue ? ExpireDate.Value < DateTime.Now : null;
        public string? Status { get; set; }
        public string DisplayText =>
            $"{Name}" + (
                Status != null ? $" ({Status})" :
                ExpireDate.HasValue ? $" ({ExpireDate.Value.ToString("D", CultureInfo.CurrentCulture)})" : ""
            );
    }
}
