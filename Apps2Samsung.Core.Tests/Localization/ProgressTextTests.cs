using Apps2Samsung.Localization;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Localization;

/// <summary>
/// Core reports progress as en.json keys (<see cref="ProgressText"/>); a head turns them into its
/// language. Every key Core emits must exist in en.json, or the phone would show the key itself.
/// </summary>
public class ProgressTextTests
{
    private static readonly LocalizationCatalog English = new();

    [Fact]
    public void KeyWithArgumentsRoundTrips()
    {
        Assert.Equal("statusInstallingOnTvPercent|42", ProgressText.Key("statusInstallingOnTvPercent", 42));
        Assert.Equal("statusPackageIdRenamed|a.b|c.d", ProgressText.Key("statusPackageIdRenamed", "a.b", "c.d"));
        Assert.Equal("statusInstallingOnTv", ProgressText.Key("statusInstallingOnTv"));
    }

    [Fact]
    public void LocalizesThroughTheCatalog()
    {
        Assert.Equal("Installing on TV… 42%", ProgressText.Localize("statusInstallingOnTvPercent|42", English.GetString));
        Assert.Equal("Installing on TV…", ProgressText.Localize("statusInstallingOnTv", English.GetString));
        Assert.Equal("Connecting through TV-Store's relay (port 26201)…", ProgressText.Localize("statusConnectingThroughRelay|26201", English.GetString));
        Assert.Equal("Package id a.b → c.d on this TV (remembered for updates).", ProgressText.Localize("statusPackageIdRenamed|a.b|c.d", English.GetString));
    }

    [Fact]
    public void SentencesAndUnknownKeysPassThrough()
    {
        Assert.Equal("Could not connect to 192.168.1.50:26101.", ProgressText.Localize("Could not connect to 192.168.1.50:26101.", English.GetString));
        Assert.Equal("notAKey", ProgressText.Localize("notAKey", English.GetString));
        Assert.Equal("notAKey|1", ProgressText.Localize("notAKey|1", English.GetString));
        Assert.Equal(string.Empty, ProgressText.Localize(null, English.GetString));
    }

    [Theory]
    [InlineData("statusConnectingToTv")]
    [InlineData("statusConnectingThroughRelay")]
    [InlineData("statusReadingTvCapabilities")]
    [InlineData("statusAuthorizingDevice")]
    [InlineData("statusApplyingTvAppChannels")]
    [InlineData("statusApplyingCustomizations")]
    [InlineData("statusResigningPackage")]
    [InlineData("statusRemovingOldVersion")]
    [InlineData("statusInstallingOnTv")]
    [InlineData("statusInstallingOnTvPercent")]
    [InlineData("statusLaunchingOnTv")]
    [InlineData("statusInstallRetryRemoveOld")]
    [InlineData("statusPackageIdForgotten")]
    [InlineData("statusPackageIdRenamed")]
    [InlineData("statusPackageIdSeparateApp")]
    public void EveryProgressKeyCoreEmitsIsInEnglish(string key)
    {
        Assert.NotEqual(key, English.GetString(key));
    }

    [Fact]
    public void TvInfoRowsAreWordedThroughTheCatalog()
    {
        var info = new TizenDeviceInfo(
            IpAddress: "192.168.1.50", DeviceName: "Woonkamer", ModelName: "UE55", Manufacturer: "Samsung",
            TizenVersion: "6.5", Duid: "XTCJ", SdkToolPath: "", DeveloperMode: "1", DeveloperIp: "127.0.0.1",
            DebugPortOpen: false, LocalIp: null, IsEmulator: false);

        var rows = info.RowsFor(English.GetString);
        var byLabel = rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("Woonkamer", byLabel["Device name"]);
        Assert.Equal("On", byLabel["Developer mode"]);
        Assert.Equal("Closed", byLabel["Debug port (26101)"]);
        Assert.Equal("No", byLabel["Emulator"]);
        Assert.Equal("not available", byLabel["Local IP"]);
        Assert.Equal("not available", byLabel["SDK tool path"]);
        Assert.StartsWith("TV-Store relay, port 26201", byLabel["Connects via"]);

        // Every label and value key exists: with a key-returning localizer nothing comes back changed...
        var keyed = info.RowsFor(k => k);
        foreach (var row in keyed)
        {
            Assert.NotEqual(row.Label, English.GetString(row.Label));
            if (row.Value.StartsWith("info", StringComparison.Ordinal) || row.Value == "keyNo")
                Assert.NotEqual(row.Value, English.GetString(row.Value));
        }
    }
}
