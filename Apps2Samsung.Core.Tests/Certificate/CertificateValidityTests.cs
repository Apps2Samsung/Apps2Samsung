using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Tests.Certificate;

public sealed class CertificateValidityTests : IDisposable
{
    private const string Password = "test-password";
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-cert-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // A self-signed .p12 valid from notBefore to notAfter, as the Samsung profiles are stored.
    private string P12(DateTime notBefore, DateTime notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Apps2Samsung test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.p12");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, Password));
        return path;
    }

    [Fact]
    public void Check_InsideTheWindow_IsValid()
    {
        var result = CertificateValidity.Check(P12(Now.AddDays(-1), Now.AddYears(1)), Password, Now);

        Assert.Equal(CertificateValidityState.Valid, result.State);
        Assert.False(result.IsNotYetValid);
    }

    [Fact]
    public void Check_PastTheEnd_IsExpired()
    {
        var result = CertificateValidity.Check(P12(Now.AddYears(-2), Now.AddDays(-1)), Password, Now);

        Assert.Equal(CertificateValidityState.Expired, result.State);
    }

    [Fact]
    public void Check_BeforeTheStart_IsNotYetValid_WithTheTimeLeft()
    {
        // What a PC clock running behind Samsung's servers produces.
        var result = CertificateValidity.Check(P12(Now.AddHours(2), Now.AddYears(1)), Password, Now);

        Assert.Equal(CertificateValidityState.NotYetValid, result.State);
        Assert.True(result.IsNotYetValid);
        var remaining = result.RemainingUntilValid(Now);
        Assert.InRange(remaining, TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Check_WrongPassword_IsUnreadableNotAFailure()
    {
        var result = CertificateValidity.Check(P12(Now.AddDays(-1), Now.AddYears(1)), "wrong", Now);

        Assert.Equal(CertificateValidityState.Unreadable, result.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing.p12")]
    public void Check_NoFile_IsUnreadable(string name)
    {
        var path = name.Length == 0 ? name : Path.Combine(_dir, name);

        Assert.Equal(CertificateValidityState.Unreadable, CertificateValidity.Check(path, Password, Now).State);
    }
}
