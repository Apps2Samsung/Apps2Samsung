using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

public class TizenInstallDiagnosticsTests
{
    [Theory]
    [InlineData("app_id[Pkg.App] installing[100]\nspend time for pkgcmd is [1520]ms", true)]
    [InlineData("install completed", true)]
    [InlineData("app_id[Pkg.App] installing[88]", false)]
    public void IndicatesSuccess(string output, bool expected)
    {
        Assert.Equal(expected, TizenInstallDiagnostics.IndicatesSuccess(output));
    }

    [Theory]
    // Tizen's helper exits 0 even when the install failed: the text decides.
    [InlineData("app_id[Pkg.App] install failed[118]\ncmd_ret:0", true)]
    [InlineData("app_id[Pkg.App] install failed[118, -22]", true)]
    [InlineData("key[end] val[fail]", true)]
    [InlineData("download failed[116]", true)]
    [InlineData("error: transport connection lost", true)]
    [InlineData("read: Connection reset by peer", true)]
    // A bare "failed" in an unrelated line is not an install failure.
    [InlineData("Pull skipped or failed\napp_id[Pkg.App] installing[100]", false)]
    [InlineData("app_id[Pkg.App] installing[100]", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IndicatesFailure(string? output, bool expected)
    {
        Assert.Equal(expected, TizenInstallDiagnostics.IndicatesFailure(output));
    }

    [Fact]
    public void OutOfSpace_Is116()
    {
        Assert.True(TizenInstallDiagnostics.IsInsufficientSpace("app_id[Pkg.App] download failed[116]"));
        Assert.False(TizenInstallDiagnostics.IsInsufficientSpace("install failed[118]"));
    }

    [Theory]
    [InlineData("install failed[118012]")]
    [InlineData("install failed[118, -12]")]
    [InlineData("app_id[MoonfinApp.moonfin] install start app_id[MoonfinApp.moonfin] installing[8] install failed[118, -11], reason: Author certificate not match :")]
    [InlineData("install failed[118011]")]
    // The one validator reason that IS the installed-copy case (#534).
    [InlineData("app_id[Pkg.App] install failed[118, -12], reason: Check certificate error : :Package with the same id and different certificate has been already installed")]
    public void CertificateMismatch(string output)
    {
        Assert.True(TizenInstallDiagnostics.IsCertificateMismatch(output));
        Assert.False(TizenInstallDiagnostics.IsPackageIdBlocked(output));
        Assert.Equal(CertificateRejection.None, TizenInstallDiagnostics.GetCertificateRejection(output));
    }

    // The TV rejecting the certificate itself (#907, #142): not a mismatch, nothing to remove.
    [Theory]
    [InlineData("app_id[MoonfinApp.moonfin] install failed[118, -12], reason: Check certificate error : :Invalid format of certificate in signature.:<-2>", CertificateRejection.InvalidFormat, "Invalid format of certificate in signature.")]
    [InlineData("app_id[AprZAARZ4r.Jellyfin] install failed[118, -12], reason: Check certificate error : :Certificate in signature is not valid yet.:<-7>", CertificateRejection.NotYetValid, "Certificate in signature is not valid yet.")]
    [InlineData("install failed[118, -12], reason: Check certificate error : :Certificate in signature is expired.:<-6>", CertificateRejection.Expired, "Certificate in signature is expired.")]
    [InlineData("install failed[118, -12], reason: Check certificate error : :Invalid certificate chain in signature.:<-3>", CertificateRejection.InvalidChain, "Invalid certificate chain in signature.")]
    [InlineData("install failed[118, -12], reason: Check certificate error : :Something new.:<-9>\nspend time for wascmd is [3205]ms", CertificateRejection.Other, "Something new.")]
    public void CertificateRejected(string output, CertificateRejection expected, string reason)
    {
        Assert.Equal(expected, TizenInstallDiagnostics.GetCertificateRejection(output));
        Assert.Equal(reason, TizenInstallDiagnostics.CertificateRejectionReason(output));
        Assert.False(TizenInstallDiagnostics.IsCertificateMismatch(output));
        Assert.True(TizenInstallDiagnostics.IndicatesFailure(output));
    }

    [Fact]
    public void CertificateRejection_NeedsA118Minus12()
    {
        // The same words under another code are not a certificate rejection.
        Assert.Equal(CertificateRejection.None,
            TizenInstallDiagnostics.GetCertificateRejection("install failed[118, -22], reason: Check certificate error : :Invalid format of certificate in signature.:<-2>"));
        Assert.Null(TizenInstallDiagnostics.CertificateRejectionReason("install failed[118]"));
    }

    [Theory]
    [InlineData("app_id[Pkg.App] install failed[118, -22]")]
    // Tizen 4 spells the same refusal as six digits.
    [InlineData("app_id[Pkg.App] install failed[118022]")]
    public void PackageIdBlocked(string output)
    {
        Assert.True(TizenInstallDiagnostics.IsPackageIdBlocked(output));
        // Not the plain [118] conflict: the heads check "blocked" first and handle it differently.
        Assert.False(TizenInstallDiagnostics.IsPackageIdConflict(output));
    }

    [Fact]
    public void PlainConflict_Is118()
    {
        const string output = "app_id[Pkg.App] install failed[118]";

        Assert.True(TizenInstallDiagnostics.IsPackageIdConflict(output));
        Assert.False(TizenInstallDiagnostics.IsPackageIdBlocked(output));
        Assert.False(TizenInstallDiagnostics.IsCertificateMismatch(output));
    }

    [Fact]
    public void ApiVersionMismatch_Is118Minus4()
    {
        Assert.True(TizenInstallDiagnostics.IsApiVersionMismatch("install failed[118, -4]"));
        Assert.False(TizenInstallDiagnostics.IsApiVersionMismatch("install failed[118, -22]"));
    }

    [Fact]
    public void Matching_IgnoresCase()
    {
        Assert.True(TizenInstallDiagnostics.IsInsufficientSpace("DOWNLOAD FAILED[116]"));
        Assert.True(TizenInstallDiagnostics.IsTransportLost("Transport Connection lost"));
    }
}
