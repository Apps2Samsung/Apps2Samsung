using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Tests.Certificate;

public class CertificateKindTests
{
    [Theory]
    [InlineData("Samsung", "Samsung")]
    [InlineData("Tizen", "Tizen")]
    [InlineData(null, "Samsung")]
    [InlineData("", "Samsung")]
    [InlineData("tizen", "Samsung")]
    public void Normalize(string? stored, string expected)
    {
        Assert.Equal(expected, CertificateKind.Normalize(stored));
        Assert.Equal(expected == "Tizen", CertificateKind.IsTizen(stored));
    }

    [Theory]
    [InlineData("Automatic", false, TizenDistributorLevel.Public)]
    [InlineData("Automatic", true, TizenDistributorLevel.Partner)]
    [InlineData("Public", false, TizenDistributorLevel.Public)]
    [InlineData("Public", true, TizenDistributorLevel.Partner)]
    [InlineData("Partner", false, TizenDistributorLevel.Partner)]
    [InlineData(null, false, TizenDistributorLevel.Public)]
    public void TizenLevel_follows_the_level_switch(string? preference, bool requirePartner, TizenDistributorLevel expected)
    {
        Assert.Equal(expected, CertificateKind.TizenLevel(preference, requirePartner));
    }

    [Fact]
    public void Tizen_sheet_has_two_steps_and_its_own_badge()
    {
        Assert.Equal(new[] { CertificateStage.Create, CertificateStage.Save }, CertificateStages.TizenSteps.Select(s => s.Stage));
        Assert.Equal("certLevelTizenPublic", CertificateStages.TizenLevelKey(TizenDistributorLevel.Public));
        Assert.Equal("certLevelTizenPartner", CertificateStages.TizenLevelKey(TizenDistributorLevel.Partner));
    }
}
