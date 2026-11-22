using Apps2Samsung.Install;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Install;

public class InstallFailedExceptionTests
{
    [Theory]
    [InlineData(CertificateRejection.InvalidFormat, InstallFailure.CertificateInvalidFormat)]
    [InlineData(CertificateRejection.NotYetValid, InstallFailure.CertificateNotYetValid)]
    [InlineData(CertificateRejection.Expired, InstallFailure.CertificateExpired)]
    [InlineData(CertificateRejection.InvalidChain, InstallFailure.CertificateChainRejected)]
    [InlineData(CertificateRejection.Other, InstallFailure.CertificateRejected)]
    public void A_certificate_rejection_has_its_own_kind(CertificateRejection rejection, InstallFailure expected)
    {
        Assert.Equal(expected, InstallFailedException.For(rejection));
    }

    // The TV's real lines, as TizenInstallDiagnostics reads them, end up as the kind a head localizes.
    [Theory]
    [InlineData("install failed[118, -12], reason: Check certificate error : :Invalid format of certificate in signature.:<-1>", InstallFailure.CertificateInvalidFormat)]
    [InlineData("install failed[118, -12], reason: Check certificate error : ", InstallFailure.CertificateRejected)]
    public void The_tvs_certificate_line_maps_to_a_kind(string output, InstallFailure expected)
    {
        Assert.Equal(expected, InstallFailedException.For(TizenInstallDiagnostics.GetCertificateRejection(output)));
    }

    [Fact]
    public void The_message_and_the_base_type_are_what_callers_always_got()
    {
        var ex = new InstallFailedException(InstallFailure.InsufficientSpace, "Not enough free space on the TV [116].") { Output = "raw" };

        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("Not enough free space on the TV [116].", ex.Message);
        Assert.Equal(InstallFailure.InsufficientSpace, ex.Failure);
        Assert.Equal("raw", ex.Output);
    }

    [Fact]
    public void A_refused_chain_is_an_install_failure_with_the_tvs_reason()
    {
        InstallFailedException ex = new CertificateChainRejectedException("refused", "Invalid certificate chain");

        Assert.Equal(InstallFailure.CertificateChainRejected, ex.Failure);
        Assert.Equal("Invalid certificate chain", ex.Reason);
    }
}
