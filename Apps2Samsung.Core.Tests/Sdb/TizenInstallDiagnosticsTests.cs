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
    public void CertificateMismatch(string output)
    {
        Assert.True(TizenInstallDiagnostics.IsCertificateMismatch(output));
        Assert.False(TizenInstallDiagnostics.IsPackageIdBlocked(output));
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
