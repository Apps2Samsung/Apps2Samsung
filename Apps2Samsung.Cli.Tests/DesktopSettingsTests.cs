using System.Text.Json.Nodes;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Cli.Tests;

// One class on purpose: CliPaths is static, so these must not run in parallel with each other.
public sealed class DesktopSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-cli-").FullName;

    public DesktopSettingsTests()
    {
        CliPaths.Use(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsPath => Path.Combine(_dir, "settings.json");
    private string LegacyRenamesPath => Path.Combine(_dir, "cli", "package-id-renames.json");

    private static string Renames(params (string Duid, string Original, string New)[] records) =>
        PackageIdRenameStore.Write(records.Select(r => new PackageIdRenameRecord
        {
            TvDuid = r.Duid,
            OriginalPackageId = r.Original,
            NewPackageId = r.New,
        }).ToList());

    private void WriteSettings(JsonObject settings) => File.WriteAllText(SettingsPath, settings.ToJsonString());

    [Fact]
    public void NoSettingsFile_ReadsAsEmpty()
    {
        var settings = new DesktopSettings(_dir);

        Assert.Equal(string.Empty, settings.LocalIp);
        Assert.Equal(string.Empty, settings.UserCustomIp);
        Assert.Equal(string.Empty, settings.ManualDuids);
        Assert.Equal("Automatic", settings.CertificatePreference);
        Assert.Equal(string.Empty, settings.PackageIdRenamesJson);
    }

    [Fact]
    public void ReadsTheDesktopsChoices()
    {
        WriteSettings(new JsonObject
        {
            ["LocalIp"] = " 192.168.2.10 ",
            ["UserCustomIP"] = "192.168.2.50",
            ["ManualDuids"] = "ABC,DEF",
            ["CertificatePreference"] = "Public",
        });

        var settings = new DesktopSettings(_dir);

        Assert.Equal("192.168.2.10", settings.LocalIp);
        Assert.Equal("192.168.2.50", settings.UserCustomIp);
        Assert.Equal("ABC,DEF", settings.ManualDuids);
        Assert.Equal("Public", settings.CertificatePreference);
    }

    [Theory]
    // A settings file from before the three-way choice only has the old toggle.
    [InlineData(null, true, "Partner")]
    [InlineData(null, false, "Automatic")]
    // Once the choice exists, the old toggle no longer decides.
    [InlineData("Public", true, "Public")]
    [InlineData("nonsense", false, "Automatic")]
    public void CertificatePreference(string? preference, bool partnerSigning, string expected)
    {
        var json = new JsonObject { ["PartnerSigning"] = partnerSigning };
        if (preference is not null)
            json["CertificatePreference"] = preference;
        WriteSettings(json);

        Assert.Equal(expected, new DesktopSettings(_dir).CertificatePreference);
    }

    [Fact]
    public void WritingRenames_KeepsTheOtherSettings()
    {
        WriteSettings(new JsonObject { ["LocalIp"] = "10.0.0.2", ["OpenAfterInstall"] = true, ["Theme"] = "Dark" });

        new DesktopSettings(_dir).PackageIdRenamesJson = Renames(("TV1", "Pkg", "New"));

        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal("10.0.0.2", (string?)saved["LocalIp"]);
        Assert.True((bool?)saved["OpenAfterInstall"]);
        Assert.Equal("Dark", (string?)saved["Theme"]);
        Assert.NotNull(PackageIdRenameStore.Find((string?)saved["PackageIdRenamesJson"], "TV1", "Pkg"));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void WritingRenames_NoSettingsFile_CreatesOne()
    {
        new DesktopSettings(_dir).PackageIdRenamesJson = Renames(("TV1", "Pkg", "New"));

        Assert.NotNull(PackageIdRenameStore.Find(new DesktopSettings(_dir).PackageIdRenamesJson, "TV1", "Pkg"));
    }

    [Fact]
    public void UnreadableSettingsFile_IsNeverWrittenOver()
    {
        const string broken = "{ \"LocalIp\": \"10.0.0.2\", ";
        File.WriteAllText(SettingsPath, broken);
        var settings = new DesktopSettings(_dir);

        Assert.Throws<InvalidOperationException>(() => settings.PackageIdRenamesJson = Renames(("TV1", "Pkg", "New")));
        Assert.Equal(broken, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void LegacyCliRenames_AreFoldedIn_DesktopRecordWins()
    {
        WriteSettings(new JsonObject { ["PackageIdRenamesJson"] = Renames(("TV1", "A", "FromDesktop")) });
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyRenamesPath)!);
        File.WriteAllText(LegacyRenamesPath, Renames(("TV1", "A", "FromCli"), ("TV2", "B", "OnlyInCli")));

        var json = new CliAppConfig(new DesktopSettings(_dir)).PackageIdRenamesJson;

        Assert.Equal("FromDesktop", PackageIdRenameStore.Find(json, "TV1", "A")!.NewPackageId);
        Assert.Equal("OnlyInCli", PackageIdRenameStore.Find(json, "TV2", "B")!.NewPackageId);
        Assert.False(File.Exists(LegacyRenamesPath));
        // Saved where the desktop reads it, not only returned.
        Assert.Equal(2, PackageIdRenameStore.Read(new DesktopSettings(_dir).PackageIdRenamesJson).Count);
    }

    [Fact]
    public void LegacyCliRenames_UnreadableSettings_StayWhereTheyAre()
    {
        File.WriteAllText(SettingsPath, "not json");
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyRenamesPath)!);
        File.WriteAllText(LegacyRenamesPath, Renames(("TV2", "B", "OnlyInCli")));

        _ = new CliAppConfig(new DesktopSettings(_dir)).PackageIdRenamesJson;

        Assert.True(File.Exists(LegacyRenamesPath));
        Assert.Equal("not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void CertificatePreference_IsSavedNextToTheDesktopsOtherKeys()
    {
        // cert --import sets the preference the desktop signs with (#921); nothing else moves.
        WriteSettings(new JsonObject { ["LocalIp"] = "192.168.1.10", ["CertificatePreference"] = "Automatic" });

        new DesktopSettings(_dir).CertificatePreference = "Partner";

        var reread = new DesktopSettings(_dir);
        Assert.Equal("Partner", reread.CertificatePreference);
        Assert.Equal("192.168.1.10", reread.LocalIp);
        Assert.Equal("Partner", JsonNode.Parse(File.ReadAllText(SettingsPath))!["CertificatePreference"]!.GetValue<string>());
    }

    [Fact]
    public void CertificatePreference_UnknownValueIsStoredAsAutomatic_AndABrokenFileIsNeverWrittenOver()
    {
        new DesktopSettings(_dir).CertificatePreference = "whatever";
        Assert.Equal("Automatic", new DesktopSettings(_dir).CertificatePreference);

        File.WriteAllText(SettingsPath, "{ not json");
        Assert.Throws<InvalidOperationException>(() => new DesktopSettings(_dir).CertificatePreference = "Public");
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }
}
