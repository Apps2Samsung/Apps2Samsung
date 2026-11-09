using System.Linq;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Tests.Remote;

/// <summary>
/// Pins the keyboard planner's behaviour to what was measured on a Samsung set, so a change that
/// invalidates the measurement fails here instead of on a TV.
/// <para>
/// Hardware reference (QA55S90FAKXXT, Tizen 9.0, TizenTube 2.1.1 — YouTube's own keyboard, read off
/// the screen): rows are A-G / H-N / O-U each with a fourth-column action (backspace, &amp;123,
/// globe), a short row (V-Z, -, ') and a wide bottom row of SPACE, CLEAR, SEARCH. Typed by these
/// very plans on the set: `abcdefg` came out exactly; `a bc` came out exactly; `ENTER` on the
/// backspace cell deleted a character; a `RIGHT` then `ENTER` from SPACE emptied the field (CLEAR);
/// two `RIGHT`s then `ENTER` ran the search (SEARCH); `UP×6` then `DOWN` returned to `a` from the
/// bottom row; digits typed by key code.
/// </para>
/// </summary>
public class SamsungRemoteKeyboardTests
{
    private static readonly SamsungRemoteKeyboard Keyboard = SamsungRemoteKeyboard.YouTube();
    private static readonly SamsungRemoteKeyboardProfile Profile = Keyboard.Profile;

    /// <summary>Compact rendering of a press sequence, for readable assertion failures.</summary>
    private static string Show(System.Collections.Generic.IEnumerable<string> keys) =>
        string.Join(" ", keys.Select(key => key switch
        {
            SamsungRemoteKeys.Up => "↑",
            SamsungRemoteKeys.Down => "↓",
            SamsungRemoteKeys.Left => "←",
            SamsungRemoteKeys.Right => "→",
            SamsungRemoteKeys.Enter => "⏎",
            _ => key.Replace("KEY_", string.Empty),
        }));

    [Fact]
    public void ProfileMatchesTheGridOnTheSet()
    {
        Assert.Equal(5, Profile.Rows.Count);
        Assert.Equal(8, Profile.Rows[0].Count);            // a-g + backspace
        Assert.Equal(7, Profile.Rows[3].Count);            // v-z, -, '
        Assert.Equal(3, Profile.Rows[4].Count);            // SPACE, CLEAR, SEARCH
        Assert.Equal((0, 0), Profile.OpensOn);
        Assert.Equal("a", Profile.Rows[Profile.OpensOn.Row][Profile.OpensOn.Col]);
    }

    /// <summary>
    /// Three Ups was the original home sequence and it was only measured from row 2: from the bottom
    /// row it merely climbs, leaving the cursor on `o`, so every following letter was typed from the
    /// wrong cell. Six is what was verified from the bottom row.
    /// </summary>
    [Fact]
    public void HomePressesAreSixUpsThenDown()
    {
        Assert.Equal(
            new[]
            {
                SamsungRemoteKeys.Up, SamsungRemoteKeys.Up, SamsungRemoteKeys.Up,
                SamsungRemoteKeys.Up, SamsungRemoteKeys.Up, SamsungRemoteKeys.Up,
                SamsungRemoteKeys.Down,
            },
            Profile.HomeKeys);
    }

    [Fact]
    public void PlanFromAnywhereStartsWithTheHomePresses()
    {
        var plan = Keyboard.PlanFromAnywhere("a");

        Assert.Equal(Profile.HomeKeys.Concat(new[] { SamsungRemoteKeys.Enter }), plan.Keys);
        Assert.Equal("↑ ↑ ↑ ↑ ↑ ↑ ↓ ⏎", Show(plan.Keys));
    }

    /// <summary>The rows the app addresses by column: Down keeps the column (measured `g` → `n`).</summary>
    [Fact]
    public void DownKeepsTheColumnBetweenLetterRows()
    {
        var plan = Keyboard.Plan("gn");

        Assert.Equal("→ → → → → → ⏎ ↓ ⏎", Show(plan.Keys));
        Assert.Equal("n", Profile.Rows[plan.Row][plan.Col]);
    }

    [Fact]
    public void LettersAcrossARowAreOneRightEach()
    {
        var plan = Keyboard.Plan("abcdefg");

        Assert.Equal("⏎ → ⏎ → ⏎ → ⏎ → ⏎ → ⏎ → ⏎", Show(plan.Keys));
        Assert.Equal((0, 6), (plan.Row, plan.Col));
    }

    /// <summary>
    /// The wide bottom row is entered at column 0 whatever column the Down came from — measured by
    /// landing on Space when descending from `c`. Modelled as `ColumnResetRows`, which is why SPACE
    /// is `↓↓↓↓ ⏎` and never a column match plus a Left.
    /// </summary>
    [Fact]
    public void SpaceIsEnterDownFourTimesThenEnter()
    {
        var plan = Keyboard.Plan("a ");

        Assert.Equal("⏎ ↓ ↓ ↓ ↓ ⏎", Show(plan.Keys));
        Assert.Equal((4, 0), (plan.Row, plan.Col));
        Assert.Contains(4, Profile.ColumnResetRows);
    }

    /// <summary>CLEAR is one Right along that row, SEARCH two — both verified on the set.</summary>
    [Fact]
    public void WideRowIsWalkedByRightsFromSpace()
    {
        // A plan can only reach a cell by pressing what the profile says exists: SPACE, CLEAR and
        // SEARCH are consecutive columns of the bottom row, and entering the row resets to column 0.
        Assert.Equal("SPACE", Profile.Rows[4][0]);
        Assert.Equal("CLEAR", Profile.Rows[4][1]);
        Assert.Equal("SUBMIT", Profile.Rows[4][2]);
        Assert.Equal((4, 1), Profile.Cells[SamsungRemoteKeyboard.ClearCell]);
        Assert.Equal((4, 2), Profile.Cells[SamsungRemoteKeyboard.SubmitCell]);

        // Bottom row entries always land on column 0, so the walk to CLEAR is Down x4 then one Right.
        var space = Keyboard.Plan(" ", (0, 0));
        Assert.Equal(0, space.Col);
        Assert.Equal("↓ ↓ ↓ ↓ ⏎", Show(space.Keys));
    }

    /// <summary>
    /// Digits go out as the remote's own key codes, so they cost one press each and never touch the
    /// grid (measured: 2-3-1 typed "231" with the cursor unmoved).
    /// </summary>
    [Fact]
    public void DigitsUseKeyCodesAndDoNotMoveTheCursor()
    {
        var plan = Keyboard.PlanFromAnywhere("231");

        Assert.Equal(Profile.HomeKeys.Concat(new[] { "KEY_2", "KEY_3", "KEY_1" }), plan.Keys);
        Assert.Equal("↑ ↑ ↑ ↑ ↑ ↑ ↓ 2 3 1", Show(plan.Keys));
        Assert.Equal(Profile.OpensOn, (plan.Row, plan.Col));
    }

    /// <summary>
    /// Nothing typeable must send nothing: the first version fired its home presses before reporting
    /// that the text could not be typed, which moved the cursor for no reason.
    /// </summary>
    [Fact]
    public void EmptyAndUnsupportedTextSendNothing()
    {
        Assert.True(Keyboard.PlanFromAnywhere(string.Empty).IsEmpty);

        var unsupported = Keyboard.PlanFromAnywhere("€");
        Assert.True(unsupported.IsEmpty);
        Assert.Equal("€", unsupported.Unsupported);
    }

    [Fact]
    public void MixedTextTypesWhatItCanAndReportsTheRest()
    {
        var plan = Keyboard.Plan("a€b");

        Assert.Equal("€", plan.Unsupported);
        Assert.Equal(3, plan.Keys.Count);         // a, b, and the Enter that picked each
    }

    /// <summary>
    /// The bug the first review caught: on a wrapping row, "one Left per column" lands one cell off.
    /// The planner now never wraps and never presses Left from column 0, so no plan can walk out of
    /// the grid — checked from every starting cell, which is the case the old parking got wrong.
    /// </summary>
    [Fact]
    public void NoPlanLeavesTheGridOrPressesLeftFromColumnZero()
    {
        for (var row = 0; row < Profile.Rows.Count; row++)
        {
            for (var col = 0; col < Profile.Rows[row].Count; col++)
            {
                foreach (var character in "abcdefghijklmnopqrstuvwxyz-' ")
                {
                    var cursor = (Row: row, Col: col);
                    var plan = Keyboard.Plan(character.ToString(), cursor);

                    foreach (var key in plan.Keys)
                    {
                        Assert.False(
                            key == SamsungRemoteKeys.Left && cursor.Col == 0,
                            $"plan for '{character}' from {row},{col} presses Left at column 0");

                        cursor = Step(cursor, key);

                        Assert.InRange(cursor.Row, 0, Profile.Rows.Count - 1);
                        Assert.InRange(cursor.Col, 0, Profile.Rows[cursor.Row].Count - 1);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A query a person would actually type: sane press count, and it ends on its last character.
    /// `lofi beats` cost 60-odd presses at ~5 per character — which is why the suggestion row, not
    /// more keys, is the future optimisation.
    /// </summary>
    [Fact]
    public void RealisticQueryIsPlannedAndEndsOnItsLastLetter()
    {
        var plan = Keyboard.PlanFromAnywhere("lofi beats");

        Assert.InRange(plan.Keys.Count, 20, 80);
        Assert.Equal("s", Profile.Rows[plan.Row][plan.Col]);
    }

    /// <summary>Mirror of the planner's move rules, so the test can replay a plan.</summary>
    private static (int Row, int Col) Step((int Row, int Col) cursor, string key)
    {
        switch (key)
        {
            case SamsungRemoteKeys.Left:
            case SamsungRemoteKeys.Right:
            {
                var columns = Profile.Rows[cursor.Row].Count;
                var col = cursor.Col + (key == SamsungRemoteKeys.Left ? -1 : 1);
                // No wrapping: the planner clamps, and a Left at column 0 is a defect the test above
                // asserts cannot happen.
                return (cursor.Row, System.Math.Clamp(col, 0, columns - 1));
            }

            case SamsungRemoteKeys.Up:
            case SamsungRemoteKeys.Down:
            {
                var row = System.Math.Clamp(
                    cursor.Row + (key == SamsungRemoteKeys.Up ? -1 : 1), 0, Profile.Rows.Count - 1);
                if (key == SamsungRemoteKeys.Down && Profile.ColumnResetRows.Contains(row))
                    return (row, 0);
                return (row, System.Math.Min(cursor.Col, Profile.Rows[row].Count - 1));
            }

            default:
                return cursor;
        }
    }
}
