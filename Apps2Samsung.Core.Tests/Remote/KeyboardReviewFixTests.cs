using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Tests.Remote;

/// <summary>
/// Regression tests for the review of the first merge: each one fails against the code as merged.
/// </summary>
public class KeyboardReviewFixTests
{
    private static readonly SamsungRemoteKeyboard Keyboard = SamsungRemoteKeyboard.YouTube();

    /// <summary>
    /// A profile with no CLEAR cell must not produce a plan that only moves the cursor: callers treat a
    /// non-empty plan as "it happened", so the clear button used to report success while pressing nothing.
    /// </summary>
    [Fact]
    public void PressingACellTheProfileDoesNotHaveIsAnEmptyPlan()
    {
        // A profile with no CLEAR at all: not in `cells`, and not as a token in a row either.
        var withoutClear = new SamsungRemoteKeyboardProfile(
            Id: "no-clear",
            Rows: new[] { new[] { "abc", "SPACE" } },
            Cells: new Dictionary<string, (int Row, int Col)> { [SamsungRemoteKeyboard.SpaceCell] = (0, 1) },
            OpensOn: (0, 0),
            HomeKeys: new[] { SamsungRemoteKeys.Up, SamsungRemoteKeys.Down });

        var plan = SamsungRemoteKeyboard.For(withoutClear).PlanPress(SamsungRemoteKeyboard.ClearCell);

        Assert.True(plan.IsEmpty);
        Assert.Equal(SamsungRemoteKeyboard.ClearCell, plan.Unsupported);
    }

    /// <summary>
    /// The wide bottom row resets the column on entry. It was only ever tested from column 0, where a
    /// column-keeping implementation looks identical — so this starts at `c` (column 2).
    /// </summary>
    [Fact]
    public void EnteringTheWideRowResetsTheColumnFromANonZeroColumn()
    {
        var plan = Keyboard.Plan("c ");

        Assert.Equal("→ → ⏎ ↓ ↓ ↓ ↓ ⏎", Show(plan.Keys));
        Assert.Equal((4, 0), (plan.Row, plan.Col));
        Assert.Equal("SPACE", Keyboard.Profile.Rows[plan.Row][plan.Col]);
    }

    /// <summary>
    /// A wrong JSON *type* used to throw out of `TryParse` (GetValue&lt;T&gt; does not report), which reached
    /// the caller as a crash rather than a message.
    /// </summary>
    [Theory]
    [InlineData(@"{ ""rows"": [""abc"", 5], ""opensOn"": [0,0] }")]
    [InlineData(@"{ ""id"": 7, ""rows"": [""abc""], ""opensOn"": [0,0] }")]
    [InlineData(@"{ ""rows"": [""abc""], ""opensOn"": [0,0], ""wrapHorizontal"": ""yes"" }")]
    [InlineData(@"{ ""rows"": [""abc""], ""opensOn"": [0,0], ""cells"": { ""SPACE"": [""a"",""b""] } }")]
    [InlineData(@"{ ""rows"": [""abc""], ""opensOn"": [0,0], ""homeKeys"": [1] }")]
    public void WrongJsonTypesAreReportedNotThrown(string json)
    {
        var threw = Record.Exception(() => SamsungRemoteKeyboardJson.TryParse(json, out _, out _));

        Assert.Null(threw);
        Assert.False(SamsungRemoteKeyboardJson.TryParse(json, out var profile, out var error));
        Assert.Null(profile);
        Assert.NotNull(error);
    }

    /// <summary>A column typo must not allocate a row with a million cells.</summary>
    [Fact]
    public void AbsurdCellColumnsAreRejected()
    {
        const string json = @"{ ""rows"": [""abc""], ""opensOn"": [0,0], ""cells"": { ""SPACE"": [0, 1000000] } }";

        Assert.False(SamsungRemoteKeyboardJson.TryParse(json, out _, out var error));
        Assert.Contains("reasonable keyboard", error!);
    }

    /// <summary>
    /// `Unsupported` lists what the grid cannot type; uppercase still types (the search boxes are
    /// case-insensitive and every profile carries one case), which this pins so it stays deliberate.
    /// </summary>
    [Fact]
    public void UppercaseFoldsToTheSingleCaseTheGridCarries()
    {
        var plan = Keyboard.PlanFromAnywhere("Hi");

        Assert.Null(plan.Unsupported);
        Assert.Equal(Keyboard.PlanFromAnywhere("hi").Keys, plan.Keys);
    }

    /// <summary>
    /// Steps a profile cannot do are reported, not silently dropped: a grid with no CLEAR or SUBMIT must not
    /// produce a plan that looks like it cleared and searched.
    /// </summary>
    [Fact]
    public void QueryReportsStepsTheProfileCannotPerform()
    {
        var minimal = new SamsungRemoteKeyboardProfile(
            Id: "letters-only",
            Rows: new[] { new[] { "a", "b", "c" } },   // three cells, not one cell holding "abc"
            Cells: new Dictionary<string, (int Row, int Col)>(),
            OpensOn: (0, 0),
            HomeKeys: new[] { SamsungRemoteKeys.Up, SamsungRemoteKeys.Down });

        var plan = SamsungRemoteKeyboard.For(minimal).PlanQuery("a", clearFirst: true, submitAfter: true);

        Assert.False(plan.IsEmpty);                              // the character still typed
        Assert.Equal(
            new[] { SamsungRemoteKeyboard.ClearCell, SamsungRemoteKeyboard.SubmitCell },
            plan.SkippedCells);
        Assert.Equal((0, 0), (plan.Row, plan.Col));              // cursor left on the typed cell
    }

    private static string Show(IEnumerable<string> keys) => string.Join(" ", keys.Select(key => key switch
    {
        SamsungRemoteKeys.Up => "↑",
        SamsungRemoteKeys.Down => "↓",
        SamsungRemoteKeys.Left => "←",
        SamsungRemoteKeys.Right => "→",
        SamsungRemoteKeys.Enter => "⏎",
        _ => key.Replace("KEY_", string.Empty),
    }));
}
