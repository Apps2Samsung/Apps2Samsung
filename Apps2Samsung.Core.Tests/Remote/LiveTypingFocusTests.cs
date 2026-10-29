using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Tests.Remote;

/// <summary>
/// A model of YouTube's search screen as observed on a real set, used to reproduce the live-typing
/// defect and to prove the strategy that survives it.
/// <para>
/// The behaviour that matters is focus, not text: after a letter is typed into the grid, the app puts
/// focus on the **first suggestion**, so the next arrow presses navigate suggestions and the next
/// ENTER activates one. Measured symptom: typing `hi` on the set produced `helloi` — `h`, then `i`'s
/// ENTER picked the suggestion `hello`, and the remaining characters were appended to it.
/// </para>
/// </summary>
internal sealed class FakeSearchScreen
{
    private readonly SamsungRemoteKeyboardProfile _profile;
    private readonly SamsungRemoteKeyboard _keyboard;
    private (int Row, int Col) _cell;

    public FakeSearchScreen()
    {
        _keyboard = SamsungRemoteKeyboard.YouTube();
        _profile = _keyboard.Profile;
    }

    /// <summary>What is in the search box.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Where the app's focus is: the text field, the keyboard grid, or the suggestion list.</summary>
    public string Focus { get; private set; } = "field";

    /// <summary>Set when the app's search cell was pressed.</summary>
    public bool Searched { get; private set; }

    /// <summary>The app's first suggestion is built from what has been typed (as YouTube does).</summary>
    public string FirstSuggestion => Text.Length == 0 ? "hello" : Text + "ello";

    public void Run(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Press(key);
    }

    private void Press(string key)
    {
        switch (key)
        {
            case SamsungRemoteKeys.Up:
                // Measured on the set: Up from the top row leaves the grid for the text field (which is
                // what makes "six Ups then Down" a reliable home), and Up from the suggestion row
                // returns to the field.
                if (Focus == "suggestion")
                    Focus = "field";
                else if (Focus == "grid")
                {
                    if (_cell.Row == 0)
                        Focus = "field";
                    else
                        Move(-1, 0);
                }

                return;

            case SamsungRemoteKeys.Down:
                // Down from the field enters the grid at its opening cell; inside the grid it descends.
                if (Focus == "field")
                {
                    Focus = "grid";
                    _cell = _profile.OpensOn;
                }
                else if (Focus == "grid")
                {
                    Move(1, 0);
                }

                return;

            case SamsungRemoteKeys.Left:
                if (Focus == "grid")
                    Move(0, -1);
                return;

            case SamsungRemoteKeys.Right:
                if (Focus == "grid")
                    Move(0, 1);
                return;

            case SamsungRemoteKeys.Enter:
                Activate();
                return;
        }

        // Digit key codes type wherever focus is — this is why "1 2 3" works on the real set.
        if (key.StartsWith("KEY_") && key.Length == 5 && char.IsDigit(key[4]))
        {
            Text += key[4];
            Focus = "suggestion";
        }
    }

    private void Move(int rows, int columns)
    {
        var row = System.Math.Clamp(_cell.Row + rows, 0, _profile.Rows.Count - 1);
        var columnsInRow = _profile.Rows[row].Count;
        _cell = rows != 0
            ? (row, System.Math.Min(_cell.Col, columnsInRow - 1))
            : (row, System.Math.Clamp(_cell.Col + columns, 0, columnsInRow - 1));
    }

    private void Activate()
    {
        switch (Focus)
        {
            case "suggestion":
                // ENTER while the suggestion row has focus runs the highlighted suggestion.
                Text = FirstSuggestion;
                Focus = "field";
                return;

            case "grid":
                var cell = _profile.Rows[_cell.Row][_cell.Col];
                switch (cell)
                {
                    case SamsungRemoteKeyboard.ClearCell:
                        Text = string.Empty;
                        return;
                    case SamsungRemoteKeyboard.SubmitCell:
                        Searched = true;
                        return;
                    case SamsungRemoteKeyboard.SpaceCell:
                        Text += " ";
                        Focus = "suggestion";
                        return;
                    case SamsungRemoteKeyboard.DeleteCell:
                        Text = Text.Length > 0 ? Text[..^1] : string.Empty;
                        return;
                    default:
                        if (cell.Length == 1)
                        {
                            Text += cell;
                            Focus = "suggestion";   // <- the behaviour that broke live typing
                        }

                        return;
                }
        }
    }
}

/// <summary>
/// The live-typing defect, reproduced, and the strategy that survives it.
/// </summary>
public class LiveTypingFocusTests
{
    /// <summary>
    /// The bug as reported: one continuous run that reuses the cursor between characters. `h` types,
    /// focus jumps to the suggestion, then the next character's ENTER activates it — `hello`, with the
    /// rest appended. This is what the set did, and what the old live mode did.
    /// </summary>
    [Fact]
    public void ReusingTheCursorBetweenCharactersCorruptsTheQuery()
    {
        var screen = new FakeSearchScreen();

        // "hi" as one run: home, type h, then walk right from h and type i.
        screen.Run(SamsungRemoteKeyboard.YouTube().PlanFromAnywhere("hi").Keys);

        // The model captures the hijack itself: `hi` becomes the suggestion `hello` (on the set the
        // remaining characters then appended to that text, which is the `helloi` that was reported).
        Assert.Equal("hello", screen.Text);
        Assert.NotEqual("hi", screen.Text);
    }

    /// <summary>
    /// Homing before **each** character puts focus back into the grid first, so every ENTER lands on a
    /// letter and the box ends up with exactly what was asked for.
    /// </summary>
    [Fact]
    public void HomingBeforeEachCharacterTypesExactly()
    {
        var screen = new FakeSearchScreen();
        var keyboard = SamsungRemoteKeyboard.YouTube();

        foreach (var character in "hi")
            screen.Run(keyboard.PlanFromAnywhere(character.ToString()).Keys);

        Assert.Equal("hi", screen.Text);
    }

    [Fact]
    public void HomingBeforeEachCharacterSurvivesARealisticQuery()
    {
        var screen = new FakeSearchScreen();
        var keyboard = SamsungRemoteKeyboard.YouTube();

        foreach (var character in "lofi")
            screen.Run(keyboard.PlanFromAnywhere(character.ToString()).Keys);

        Assert.Equal("lofi", screen.Text);
    }

    /// <summary>Digits go by key code, so they never involve the grid or the suggestion row.</summary>
    [Fact]
    public void DigitsAreUnaffectedByTheFocusJump()
    {
        var screen = new FakeSearchScreen();

        screen.Run(new[] { "KEY_2", "KEY_0", "KEY_2", "KEY_4" });

        Assert.Equal("2024", screen.Text);
    }

    /// <summary>Backspace is the DELETE cell, reached the same homing way, and removes one character.</summary>
    [Fact]
    public void BackspaceDeletesThroughTheGrid()
    {
        var screen = new FakeSearchScreen();
        var keyboard = SamsungRemoteKeyboard.YouTube();

        foreach (var character in "hi")
            screen.Run(keyboard.PlanFromAnywhere(character.ToString()).Keys);

        screen.Run(keyboard.PlanPress(SamsungRemoteKeyboard.DeleteCell).Keys);

        Assert.Equal("h", screen.Text);
    }

    /// <summary>Search is the SUBMIT cell, and the model records that it was pressed.</summary>
    [Fact]
    public void SearchPressesSubmit()
    {
        var screen = new FakeSearchScreen();
        var keyboard = SamsungRemoteKeyboard.YouTube();

        foreach (var character in "hi")
            screen.Run(keyboard.PlanFromAnywhere(character.ToString()).Keys);

        screen.Run(keyboard.PlanPress(SamsungRemoteKeyboard.SubmitCell).Keys);

        Assert.True(screen.Searched);
    }

    /// <summary>Clearing from wherever focus is works because CLEAR is reached the homing way too.</summary>
    [Fact]
    public void ClearEmptiesTheFieldFromTheSuggestionRow()
    {
        var screen = new FakeSearchScreen();
        var keyboard = SamsungRemoteKeyboard.YouTube();

        foreach (var character in "hi")
            screen.Run(keyboard.PlanFromAnywhere(character.ToString()).Keys);

        Assert.Equal("suggestion", screen.Focus);

        screen.Run(keyboard.PlanPress(SamsungRemoteKeyboard.ClearCell).Keys);

        Assert.Equal(string.Empty, screen.Text);
    }
}
