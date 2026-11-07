using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Tests.Remote;

/// <summary>
/// The keyboard profile is per app and user-editable, so these pin the two things that can silently
/// type the wrong letters: resolving the wrong profile for an app, and accepting a profile that
/// does not describe a usable grid.
/// </summary>
public class SamsungRemoteKeyboardProfileTests
{
    /// <summary>
    /// Every profile shipped as JSON must parse, be keyed to an app, and describe a usable grid —
    /// this is the check a contributor gets for free when they add a file.
    /// </summary>
    [Fact]
    public void EveryShippedProfileIsUsable()
    {
        Assert.NotEmpty(SamsungRemoteKeyboardProfiles.BuiltIn);

        foreach (var profile in SamsungRemoteKeyboardProfiles.BuiltIn)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.AppId), $"{profile.Id} has no appId");
            Assert.False(string.IsNullOrWhiteSpace(profile.Label), $"{profile.Id} has no label");
            Assert.InRange(profile.OpensOn.Row, 0, profile.Rows.Count - 1);
            Assert.InRange(profile.OpensOn.Col, 0, profile.Rows[profile.OpensOn.Row].Count - 1);
            Assert.NotEmpty(profile.HomeKeys);
            Assert.True(profile.Cells.ContainsKey(SamsungRemoteKeyboard.SpaceCell), $"{profile.Id} has no space cell");
        }

        // App ids must be unique, or one app would silently shadow another's profile.
        var duplicates = SamsungRemoteKeyboardProfiles.BuiltIn
            .GroupBy(p => p.AppId!, System.StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        Assert.Empty(duplicates);
    }

    /// <summary>
    /// YouTube and TizenTube are the same UI, so their geometry is duplicated on purpose (each file
    /// stands alone). This fails when one is edited without the other.
    /// </summary>
    [Fact]
    public void TizenTubeAndYouTubeShareGeometry()
    {
        var youtube = SamsungRemoteKeyboardProfiles.Find(SamsungRemoteKeyboardProfiles.YouTubeAppId)!;
        var tizentube = SamsungRemoteKeyboardProfiles.Find(SamsungRemoteKeyboardProfiles.TizenTubeAppId)!;

        Assert.Equal(youtube.Rows, tizentube.Rows);
        Assert.Equal(youtube.Cells, tizentube.Cells);
        Assert.Equal(youtube.HomeKeys, tizentube.HomeKeys);
        Assert.Equal(youtube.ColumnResetRows, tizentube.ColumnResetRows);
    }

    [Fact]
    public void YouTubeIsKeyedToItsAppId()
    {
        Assert.Equal(SamsungRemoteKeyboardProfiles.YouTubeAppId, SamsungRemoteKeyboard.YouTubeSearch.AppId);
        Assert.NotNull(SamsungRemoteKeyboardProfiles.Resolve(SamsungRemoteKeyboardProfiles.YouTubeAppId));
    }

    [Fact]
    public void TizenTubeResolvesToTheSameGrid()
    {
        var youtube = SamsungRemoteKeyboardProfiles.Resolve(SamsungRemoteKeyboardProfiles.YouTubeAppId);
        var tizentube = SamsungRemoteKeyboardProfiles.Resolve(SamsungRemoteKeyboardProfiles.TizenTubeAppId);

        Assert.NotNull(tizentube);
        Assert.Equal(youtube!.Rows, tizentube!.Rows);
        Assert.Equal(youtube.Cells, tizentube.Cells);
        Assert.Equal(tizentube.AppId, SamsungRemoteKeyboardProfiles.TizenTubeAppId);
    }

    /// <summary>
    /// An app with no profile must resolve to nothing: the remote then says it cannot type there,
    /// which is the whole point of keying profiles by app instead of guessing YouTube's grid.
    /// </summary>
    [Fact]
    public void UnknownAppHasNoProfile()
    {
        Assert.Null(SamsungRemoteKeyboardProfiles.Resolve("org.tizen.netflixapp"));
        Assert.Null(SamsungRemoteKeyboardProfiles.Resolve(null));
        Assert.Null(SamsungRemoteKeyboardProfiles.Resolve(""));
    }

    /// <summary>A profile pasted by a user for another app wins over anything built in.</summary>
    [Fact]
    public void UserProfileOverridesForItsApp()
    {
        const string netflix = @"{
          ""id"": ""netflix-search"", ""appId"": ""org.tizen.netflixapp"",
          ""rows"": [""abc"", ""def""],
          ""cells"": { ""SPACE"": [1, 3] },
          ""opensOn"": [0, 0],
          ""homeKeys"": [""KEY_UP"", ""KEY_DOWN""]
        }";

        var resolved = SamsungRemoteKeyboardProfiles.Resolve(
            "org.tizen.netflixapp",
            new[] { new KeyValuePair<string, string>("org.tizen.netflixapp", netflix) });

        Assert.NotNull(resolved);
        Assert.Equal("netflix-search", resolved!.Id);
        Assert.Equal(2, resolved.Rows.Count);
        Assert.Equal((1, 3), resolved.Cells[SamsungRemoteKeyboard.SpaceCell]);
        // The action cell's column is padded into the row so it has somewhere to live.
        Assert.Equal(4, resolved.Rows[1].Count);
    }

    /// <summary>The built-in profile survives a round trip through the editable JSON, unchanged.</summary>
    [Fact]
    public void BuiltInProfileRoundTripsThroughJson()
    {
        var original = SamsungRemoteKeyboard.YouTubeSearch;
        var json = SamsungRemoteKeyboardJson.ToJson(original);

        Assert.True(SamsungRemoteKeyboardJson.TryParse(json, out var parsed, out var error), error);

        Assert.Equal(original.Rows.Count, parsed!.Rows.Count);
        Assert.Equal(original.OpensOn, parsed.OpensOn);
        Assert.Equal(original.HomeKeys, parsed.HomeKeys);
        Assert.Equal(original.ColumnResetRows, parsed.ColumnResetRows);
        Assert.Equal(original.DirectKeys, parsed.DirectKeys);
        Assert.Equal(original.Cells.OrderBy(c => c.Key), parsed.Cells.OrderBy(c => c.Key));

        // And the parsed profile plans exactly the same presses as the original.
        var before = SamsungRemoteKeyboard.For(original).PlanFromAnywhere("a bc");
        var after = SamsungRemoteKeyboard.For(parsed).PlanFromAnywhere("a bc");
        Assert.Equal(before.Keys, after.Keys);
    }

    /// <summary>Rows stay human-readable in the JSON: action columns are not written as filler.</summary>
    [Fact]
    public void JsonWritesRowsAsPlainText()
    {
        var json = SamsungRemoteKeyboardJson.ToJson(SamsungRemoteKeyboard.YouTubeSearch);

        Assert.Contains("\"abcdefg\"", json);      // not "abcdefg·"
        Assert.Contains("\"vwxyz-'\"", json);
        Assert.Contains("\"DELETE\"", json);
        Assert.Contains("\"columnResetRows\"", json);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData(@"{ ""rows"": [] }", "non-empty array")]
    [InlineData(@"{ ""rows"": [""abc""] }", "opensOn")]
    [InlineData(@"{ ""rows"": [""abc""], ""opensOn"": [5, 0] }", "not a cell on the grid")]
    [InlineData(@"{ ""rows"": [""abc""], ""opensOn"": [0, 0], ""homeKeys"": [""UP""] }", "not a key code")]
    [InlineData(@"{ ""rows"": [""abc""], ""opensOn"": [0, 0], ""cells"": { ""SPACE"": [9, 0] } }", "not on the grid")]
    public void BadProfilesAreRejectedWithAReason(string json, string expectedInMessage)
    {
        Assert.False(SamsungRemoteKeyboardJson.TryParse(json, out var profile, out var error));
        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.Contains(expectedInMessage, error!, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An empty row is only legal when a named cell lives in it (the wide button row).</summary>
    [Fact]
    public void EmptyRowIsOnlyLegalWithANamedCellInIt()
    {
        const string noCell = @"{ ""rows"": [""abc"", """"], ""opensOn"": [0,0] }";
        Assert.False(SamsungRemoteKeyboardJson.TryParse(noCell, out _, out _));

        const string withCell = @"{ ""rows"": [""abc"", """"], ""cells"": { ""SPACE"": [1,0] }, ""opensOn"": [0,0] }";
        Assert.True(SamsungRemoteKeyboardJson.TryParse(withCell, out var parsed, out var error), error);
        Assert.Equal((1, 0), parsed!.Cells[SamsungRemoteKeyboard.SpaceCell]);
    }
}
