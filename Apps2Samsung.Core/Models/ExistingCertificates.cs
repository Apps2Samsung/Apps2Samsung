using System;
using System.Globalization;

namespace Apps2Samsung.Models
{
    public class ExistingCertificates
    {
        public required string Name { get; set; }
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
