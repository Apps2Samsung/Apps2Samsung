using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Tests.Certificate;

public class CertificatePreferenceTests
{
    [Theory]
    [InlineData("Public", "Public")]
    [InlineData("Partner", "Partner")]
    [InlineData("Automatic", "Automatic")]
    [InlineData(null, "Automatic")]
    [InlineData("", "Automatic")]
    [InlineData("partner", "Automatic")]
    [InlineData("Jelly2Sams (default)", "Automatic")]
    public void Normalize(string? stored, string expected)
    {
        Assert.Equal(expected, CertificatePreference.Normalize(stored));
    }

    [Theory]
    [InlineData("Automatic", false, CertificatePrivilegeLevel.Public)]
    [InlineData("Automatic", true, CertificatePrivilegeLevel.Partner)]
    [InlineData("Public", false, CertificatePrivilegeLevel.Public)]
    // A hard requirement wins over the preference: a Public-signed install would be refused.
    [InlineData("Public", true, CertificatePrivilegeLevel.Partner)]
    [InlineData("Partner", false, CertificatePrivilegeLevel.Partner)]
    [InlineData(null, false, CertificatePrivilegeLevel.Public)]
    public void ResolveLevel(string? preference, bool requirePartner, CertificatePrivilegeLevel expected)
    {
        Assert.Equal(expected, CertificatePreference.ResolveLevel(preference, requirePartner));
    }

    [Theory]
    [InlineData("Automatic", true, false, true)]
    [InlineData("Public", true, false, true)]
    [InlineData("Partner", true, false, false)]
    [InlineData("Automatic", true, true, false)]
    [InlineData("Automatic", false, false, false)]
    public void ShouldSwitchToPartner(string preference, bool requirePartner, bool hasPartnerProfile, bool expected)
    {
        Assert.Equal(expected, CertificatePreference.ShouldSwitchToPartner(preference, requirePartner, hasPartnerProfile));
    }

    [Theory]
    [InlineData("Automatic", false, true, "Partner")]
    [InlineData("Automatic", true, false, "Public")]
    [InlineData("Partner", true, true, "Partner")]
    [InlineData("Public", false, false, "Public")]
    [InlineData(null, true, true, "Automatic")]
    public void AfterImport(string? current, bool hasPublic, bool hasPartner, string expected)
    {
        Assert.Equal(expected, CertificatePreference.AfterImport(current, hasPublic, hasPartner));
    }
}
