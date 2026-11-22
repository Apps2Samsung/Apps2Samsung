using Apps2Samsung.Install;
using Apps2Samsung.Localization;

namespace Apps2Samsung.Tests.Install;

// The desktop reads every stopped install here (#1149): each kind has to come out as a sentence
// from the catalog, never as a key and never with a hole where a value belongs.
public class InstallFailureTextTests
{
    private static readonly LocalizationCatalog English = new();

    private static string Describe(InstallFailedException failure) =>
        InstallFailureText.Describe(failure, "Jellyfin", English.GetString);

    public static IEnumerable<object[]> Kinds() =>
        Enum.GetValues<InstallFailure>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_reads_as_a_sentence(InstallFailure kind)
    {
        var text = Describe(new InstallFailedException(kind, "The installer's own English sentence.")
        {
            Output = "install failed[118]",
            Reason = "Something the TV said.",
            RequiredTizen = "6.5",
            TvTizen = "5.0",
            InstalledVersion = "0.3.1",
        });

        Assert.Contains(' ', text);
        Assert.DoesNotContain("{0}", text);
        Assert.DoesNotContain("{1}", text);
    }

    [Fact]
    public void A_package_for_a_newer_tizen_names_both_versions()
    {
        var text = Describe(new InstallFailedException(InstallFailure.NeedsNewerTizen, "english") { RequiredTizen = "6.5", TvTizen = "5.0" });

        Assert.Contains("6.5", text);
        Assert.Contains("5.0", text);
    }

    [Theory]
    [InlineData(InstallFailure.CertificateMismatch)]
    [InlineData(InstallFailure.CertificateMismatchAfterRemoval)]
    [InlineData(InstallFailure.CertificateMismatchTvCannotRemove)]
    [InlineData(InstallFailure.PackageIdBlocked)]
    [InlineData(InstallFailure.InstalledAndTvCannotRemove)]
    public void The_texts_about_an_installed_copy_name_the_app(InstallFailure kind)
    {
        Assert.Contains("Jellyfin", Describe(new InstallFailedException(kind, "english")));
    }

    [Fact]
    public void A_rejected_certificate_carries_the_tvs_reason_or_the_code()
    {
        Assert.Contains("Something new.", Describe(new InstallFailedException(InstallFailure.CertificateRejected, "english") { Reason = "Something new." }));
        // The empty reason of #1143.
        Assert.Contains("[118, -12]", Describe(new InstallFailedException(InstallFailure.CertificateRejected, "english")));
    }

    [Fact]
    public void A_generic_failure_shows_what_the_tv_said()
    {
        Assert.Contains("install failed[61]", Describe(new InstallFailedException(InstallFailure.Generic, "Install failed: x") { Output = " install failed[61] " }));
    }

    [Fact]
    public void A_kind_without_a_text_of_its_own_keeps_the_installers_sentence()
    {
        Assert.Equal("The TV could not read the manifest.",
            Describe(new InstallFailedException(InstallFailure.ManifestUnreadable, "The TV could not read the manifest.")));
    }
}
