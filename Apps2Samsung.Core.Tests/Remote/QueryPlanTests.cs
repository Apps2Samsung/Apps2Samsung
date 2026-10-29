using System.Linq;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Tests.Remote;

/// <summary>
/// Typing alone fills the box and shows suggestions; a search needs the field emptied first (the app
/// appends) and the app's search cell pressed afterwards. These pin that composed run, which is what
/// the remote actually sends.
/// </summary>
public class QueryPlanTests
{
    private static readonly SamsungRemoteKeyboard Keyboard = SamsungRemoteKeyboard.YouTube();

    private static string Show(System.Collections.Generic.IEnumerable<string> keys) =>
        string.Join(" ", keys.Select(key => key switch
        {
            SamsungRemoteKeys.Up => "↑", SamsungRemoteKeys.Down => "↓",
            SamsungRemoteKeys.Left => "←", SamsungRemoteKeys.Right => "→",
            SamsungRemoteKeys.Enter => "⏎", _ => key.Replace("KEY_", string.Empty),
        }));

    [Fact]
    public void QueryClearsTypesThenSearches()
    {
        var plan = Keyboard.PlanQuery("a", clearFirst: true, submitAfter: true);
        var shown = Show(plan.Keys);

        // home (6 Up + its own Down, landing on 'a')
        //   → CLEAR: four Downs onto the wide row (column resets), one Right, Enter
        //   → back to 'a': four Ups, one Left (column 1 → 0, safe mid-row), Enter types 'a'
        //   → SEARCH: four Downs, two Rights, Enter
        Assert.Equal("↑ ↑ ↑ ↑ ↑ ↑ ↓ ↓ ↓ ↓ ↓ → ⏎ ↑ ↑ ↑ ↑ ← ⏎ ↓ ↓ ↓ ↓ → → ⏎", shown);
    }

    [Fact]
    public void QueryCanSkipTheClear()
    {
        var plan = Keyboard.PlanQuery("a", clearFirst: false, submitAfter: true);

        Assert.Equal("↑ ↑ ↑ ↑ ↑ ↑ ↓ ⏎ ↓ ↓ ↓ ↓ → → ⏎", Show(plan.Keys));
    }

    [Fact]
    public void QueryCanSkipTheSearch()
    {
        var plan = Keyboard.PlanQuery("a", clearFirst: true, submitAfter: false);

        Assert.Equal("↑ ↑ ↑ ↑ ↑ ↑ ↓ ↓ ↓ ↓ ↓ → ⏎ ↑ ↑ ↑ ↑ ← ⏎", Show(plan.Keys));
    }

    /// <summary>Nothing typeable must not press search over an empty box.</summary>
    [Fact]
    public void UnsupportedQuerySendsNothing()
    {
        Assert.True(Keyboard.PlanQuery("€").IsEmpty);
    }

    /// <summary>The clear button is one press target, reachable from anywhere via the home presses.</summary>
    [Fact]
    public void ClearButtonPressesTheClearCell()
    {
        var plan = Keyboard.PlanPress(SamsungRemoteKeyboard.ClearCell);

        Assert.Equal("↑ ↑ ↑ ↑ ↑ ↑ ↓ ↓ ↓ ↓ ↓ → ⏎", Show(plan.Keys));
        Assert.Equal((4, 1), (plan.Row, plan.Col));
    }

    [Fact]
    public void ClearButtonEndsOnTheClearCell()
    {
        var plan = Keyboard.PlanPress(SamsungRemoteKeyboard.ClearCell);

        Assert.Equal("CLEAR", Keyboard.Profile.Rows[plan.Row][plan.Col]);
    }
}
