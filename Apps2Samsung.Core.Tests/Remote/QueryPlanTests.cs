using System.Linq;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Tests.Remote;

/// <summary>
/// Typing a whole query: clear the field, type, press search. Each step homes first, because the app
/// moves focus to its suggestion row after every character (see <see cref="LiveTypingFocusTests"/>).
/// </summary>
public class QueryPlanTests
{
    private static readonly SamsungRemoteKeyboard Keyboard = SamsungRemoteKeyboard.YouTube();

    /// <summary>The composed run types the query on a modelled screen, which is the behaviour that matters.</summary>
    [Fact]
    public void QueryTypesExactlyOnTheModelledScreen()
    {
        var screen = new FakeSearchScreen();

        screen.Run(Keyboard.PlanQuery("hi", clearFirst: true, submitAfter: false).Keys);

        Assert.Equal("hi", screen.Text);
    }

    [Fact]
    public void QueryTypesExactlyThroughAWholeWord()
    {
        var screen = new FakeSearchScreen();

        screen.Run(Keyboard.PlanQuery("lofi", clearFirst: true, submitAfter: false).Keys);

        Assert.Equal("lofi", screen.Text);
    }

    [Fact]
    public void QueryClearsFirstSoASecondSearchDoesNotAppend()
    {
        var screen = new FakeSearchScreen();
        screen.Run(Keyboard.PlanQuery("hi", clearFirst: true, submitAfter: false).Keys);

        screen.Run(Keyboard.PlanQuery("yo", clearFirst: true, submitAfter: false).Keys);

        Assert.Equal("yo", screen.Text);
    }

    [Fact]
    public void QueryCanSkipTheClear()
    {
        var screen = new FakeSearchScreen();
        screen.Run(Keyboard.PlanQuery("hi", clearFirst: true, submitAfter: false).Keys);

        screen.Run(Keyboard.PlanQuery("ya", clearFirst: false, submitAfter: false).Keys);

        Assert.Equal("hiya", screen.Text);
    }

    [Fact]
    public void QueryCanPressSearch()
    {
        var screen = new FakeSearchScreen();

        screen.Run(Keyboard.PlanQuery("hi", clearFirst: true, submitAfter: true).Keys);

        Assert.Equal("hi", screen.Text);
        Assert.True(screen.Searched);
    }

    /// <summary>Nothing typeable must send nothing: no clear, no search over an empty box.</summary>
    [Fact]
    public void UnsupportedQuerySendsNothing()
    {
        var plan = Keyboard.PlanQuery("€");

        Assert.True(plan.IsEmpty);
        Assert.Equal("€", plan.Unsupported);
    }

    /// <summary>Each character is preceded by the profile's home presses — the fix, made explicit.</summary>
    [Fact]
    public void EveryCharacterIsPrecededByTheHomePresses()
    {
        var plan = Keyboard.PlanQuery("hi", clearFirst: false, submitAfter: false);

        var homes = CountOccurrences(plan.Keys, Keyboard.Profile.HomeKeys);
        Assert.Equal(2, homes);   // one per character
    }

    [Fact]
    public void ClearButtonPressesTheClearCell()
    {
        var plan = Keyboard.PlanPress(SamsungRemoteKeyboard.ClearCell);

        Assert.Equal("CLEAR", Keyboard.Profile.Rows[plan.Row][plan.Col]);
    }

    [Fact]
    public void ClearButtonEmptiesTheFieldFromTheSuggestionRow()
    {
        var screen = new FakeSearchScreen();
        screen.Run(Keyboard.PlanQuery("hi", clearFirst: false, submitAfter: false).Keys);

        screen.Run(Keyboard.PlanPress(SamsungRemoteKeyboard.ClearCell).Keys);

        Assert.Equal(string.Empty, screen.Text);
    }

    private static int CountOccurrences(System.Collections.Generic.IReadOnlyList<string> keys,
        System.Collections.Generic.IReadOnlyList<string> needle)
    {
        var count = 0;
        for (var i = 0; i + needle.Count <= keys.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (keys[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                count++;
        }

        return count;
    }
}
