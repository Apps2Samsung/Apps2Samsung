using System;
using System.Collections.Generic;
using System.Linq;

namespace Apps2Samsung.Remote
{
    /// <summary>What to send to type a string on an app's own on-screen keyboard.</summary>
    /// <param name="Keys">The presses, in order, ending on <paramref name="Row"/>/<paramref name="Col"/>.</param>
    /// <param name="Row">Where the app's keyboard cursor is left, so a follow-up string can continue.</param>
    /// <param name="Col">See <paramref name="Row"/>.</param>
    /// <param name="Unsupported">
    /// Characters the profile cannot produce, distinct — null when everything in the string can be typed.
    /// </param>
    public sealed record SamsungRemoteKeyboardPlan(
        IReadOnlyList<string> Keys,
        int Row,
        int Col,
        string? Unsupported)
    {
        /// <summary>True when there is nothing worth sending — an empty plan is not a zero-press run.</summary>
        public bool IsEmpty => Keys.Count == 0;
    }

    /// <summary>
    /// One app's on-screen keyboard: the cells, and where the app leaves the cursor when that keyboard
    /// opens. This is **data, not code**, on purpose — grids differ per app and per revision, and a
    /// profile that is wrong should type nothing rather than type the wrong letters silently.
    /// </summary>
    /// <param name="Id">Profile name, for diagnostics and for keying a per-app choice.</param>
    /// <param name="Rows">
    /// The cells, top row first. A cell is the character it types, or a name from <paramref name="Cells"/>
    /// for one whose label is an action (space, backspace, clear, submit).
    /// </param>
    /// <param name="Cells">Named action cells: "SPACE", "DELETE", "CLEAR", "SUBMIT" → their position.</param>
    /// <param name="OpensOn">
    /// The cell the app's cursor is on when its keyboard opens. Plans are computed from here, which is
    /// what removes the need to hunt for a corner first.
    /// </param>
    /// <param name="HomeKeys">
    /// Presses that put the cursor on <paramref name="OpensOn"/> from anywhere on the keyboard, for the
    /// case where something else moved it (the user touched the d-pad, or a previous plan ended
    /// mid-grid). Verified on hardware — never computed from the grid's dimensions: on a wrapping row
    /// "one Left per column" lands one cell off, which is exactly the bug this replaces.
    /// </param>
    /// <param name="WrapHorizontal">
    /// Whether Left/Right loop within a row. Leave false unless the app is known to wrap: with a wrong
    /// true, a plan walks the long way round and presses cells nobody asked for.
    /// </param>
    public sealed record SamsungRemoteKeyboardProfile(
        string Id,
        IReadOnlyList<IReadOnlyList<string>> Rows,
        IReadOnlyDictionary<string, (int Row, int Col)> Cells,
        (int Row, int Col) OpensOn,
        IReadOnlyList<string> HomeKeys,
        bool WrapHorizontal = false)
    {
        /// <summary>
        /// Characters typed with a real key code instead of a grid cell. Digits are the case that was
        /// measured: these apps keep numbers on a separate keyboard page, yet they do handle the Samsung
        /// remote's own digit keys — which is why 1-2-3 lands in a YouTube search box while letters
        /// never do. A populated map means those characters cost one press and move nothing.
        /// </summary>
        public IReadOnlyDictionary<char, string> DirectKeys { get; init; } = new Dictionary<char, string>();

        /// <summary>
        /// Rows that a Down press enters at column 0, whatever column it came from. Measured: on
        /// YouTube's wide bottom button row, Down from any letter column lands on SPACE, and
        /// CLEAR/SEARCH are then reached with Right. Treating that row as column-addressed would press
        /// the wrong button — which is exactly what happened before this was measured.
        /// </summary>
        public IReadOnlyCollection<int> ColumnResetRows { get; init; } = Array.Empty<int>();

        /// <summary>The app this keyboard belongs to, if it is keyed to one (e.g. YouTube's app id).</summary>
        public string? AppId { get; init; }

        /// <summary>What a picker should call this keyboard, e.g. "YouTube".</summary>
        public string? Label { get; init; }
    }

    /// <summary>
    /// Types text into an app that draws its **own** on-screen keyboard, by walking that keyboard with
    /// the d-pad (arrow presses + Enter on the cell under the cursor).
    /// <para>
    /// Why this exists next to <see cref="SamsungRemoteClient.SendTextAsync"/>: <c>SendInputString</c>
    /// reaches the TV's **system** IME, so it works in Smart Hub search, the browser and settings — but
    /// YouTube, Netflix and TizenTube paint their own keyboard inside the app, never raise the Tizen IME
    /// (no <c>ms.remote.ime*</c> events at all, measured), and ignore typed strings entirely. Digits are
    /// the exception and the tell: a Samsung remote has real digit keys, which those apps do handle, so
    /// 1-2-3 lands in a YouTube search box while letters never do. Letters exist only on the app's grid,
    /// so the grid is what has to be pressed.
    /// </para>
    /// <para>
    /// The model is deliberately literal — rows of cells, a cursor, arrows that move it, Enter that
    /// activates it — because that is what the app implements. The only non-obvious rule is the one that
    /// was measured rather than derived: moving between rows keeps the column, and horisontal moves stay
    /// inside their row (a Left at a row's start leaves the grid for the side nav, which is why nothing
    /// here ever presses Left from column 0).
    /// </para>
    /// </summary>
    public sealed class SamsungRemoteKeyboard
    {
        /// <summary>Cell name for the space bar.</summary>
        public const string SpaceCell = "SPACE";

        /// <summary>Cell name for backspace/delete.</summary>
        public const string DeleteCell = "DELETE";

        /// <summary>Cell name for the "clear the whole field" cell, where an app has one.</summary>
        public const string ClearCell = "CLEAR";

        /// <summary>Cell name for the "run the search" cell, where an app has one.</summary>
        public const string SubmitCell = "SUBMIT";

        private readonly Dictionary<string, (int Row, int Col)> _cellIndex;

        private SamsungRemoteKeyboard(SamsungRemoteKeyboardProfile profile)
        {
            Profile = profile;

            _cellIndex = new Dictionary<string, (int Row, int Col)>(StringComparer.Ordinal);
            for (var row = 0; row < profile.Rows.Count; row++)
            {
                for (var col = 0; col < profile.Rows[row].Count; col++)
                    _cellIndex[profile.Rows[row][col]] = (row, col);
            }

            foreach (var named in profile.Cells)
                _cellIndex[named.Key] = named.Value;
        }

        /// <summary>The profile in use.</summary>
        public SamsungRemoteKeyboardProfile Profile { get; }

        /// <summary>
        /// A keyboard for calling code that has a profile in hand.
        /// </summary>
        public static SamsungRemoteKeyboard For(SamsungRemoteKeyboardProfile profile) => new(profile);

        /// <summary>
        /// YouTube's profile, ready to plan with. Profiles live in <c>Remote/Keyboards/*.json</c> (see
        /// <see cref="SamsungRemoteKeyboardProfiles"/>); this is the accessor for code that specifically
        /// means YouTube, and for the tests.
        /// </summary>
        public static SamsungRemoteKeyboard YouTube() => new(YouTubeSearch);

        /// <summary>YouTube's built-in profile.</summary>
        public static SamsungRemoteKeyboardProfile YouTubeSearch =>
            SamsungRemoteKeyboardProfiles.Find(SamsungRemoteKeyboardProfiles.YouTubeAppId)
            ?? throw new InvalidOperationException("the built-in YouTube keyboard profile is missing");

        /// <summary>Builds the press sequence for <paramref name="text"/>, starting from the profile's
        /// <see cref="SamsungRemoteKeyboardProfile.OpensOn"/> cell.</summary>
        public SamsungRemoteKeyboardPlan Plan(string text) => Plan(text, Profile.OpensOn, includeHome: false);

        /// <summary>
        /// Builds the press sequence for <paramref name="text"/> without assuming the cursor is still
        /// where the app left it: the profile's measured home presses go first. When nothing in
        /// <paramref name="text"/> can be typed the plan is empty — home presses are not sent on their
        /// own, since moving a cursor and typing nothing is worse than doing nothing.
        /// </summary>
        public SamsungRemoteKeyboardPlan PlanFromAnywhere(string text) => Plan(text, Profile.OpensOn, includeHome: true);

        /// <summary>
        /// Builds the plan for actually running a search: optionally empty the field first, type, then
        /// optionally press the app's search cell. Each step navigates from wherever the previous one
        /// left the cursor, so the whole run is one continuous key sequence — which is what makes it
        /// work without any way to read the TV back.
        /// <para>
        /// Clearing first matters because the app appends to whatever is already in the box: without it a
        /// second search becomes the two queries concatenated. Pressing search matters because typing
        /// alone only fills the box and shows suggestions.
        /// </para>
        /// </summary>
        public SamsungRemoteKeyboardPlan PlanQuery(string text, bool clearFirst = true, bool submitAfter = true)
        {
            var keys = new List<string>();

            // Every step homes first, and so does every character. That is not belt-and-braces: the app
            // moves focus to its suggestion row as soon as a character is typed, so a run that keeps the
            // cursor between characters has its next ENTER land on a suggestion — typing "hi" produced the
            // first suggestion with the rest appended. Homing costs a few presses per character and makes
            // the result independent of what the app did in between. See LiveTypingFocusTests.
            if (clearFirst)
                keys.AddRange(PlanPress(ClearCell).Keys);

            var unsupported = new List<char>();
            foreach (var character in text)
            {
                var one = PlanFromAnywhere(character.ToString());
                if (one.IsEmpty)
                {
                    if (!unsupported.Contains(character))
                        unsupported.Add(character);
                    continue;
                }

                keys.AddRange(one.Keys);
            }

            if (keys.Count == (clearFirst ? PlanPress(ClearCell).Keys.Count : 0))
            {
                // Nothing typeable: send nothing at all rather than clearing the field and stopping.
                return new SamsungRemoteKeyboardPlan(
                    Array.Empty<string>(),
                    Profile.OpensOn.Row,
                    Profile.OpensOn.Col,
                    unsupported.Count == 0 ? null : new string(unsupported.ToArray()));
            }

            if (submitAfter)
                keys.AddRange(PlanPress(SubmitCell).Keys);

            var last = PlanPress(SubmitCell);
            return new SamsungRemoteKeyboardPlan(
                keys,
                submitAfter ? last.Row : Profile.OpensOn.Row,
                submitAfter ? last.Col : Profile.OpensOn.Col,
                unsupported.Count == 0 ? null : new string(unsupported.ToArray()));
        }

        /// <summary>Plan for pressing one named cell (e.g. CLEAR) from the app's opening position.</summary>
        public SamsungRemoteKeyboardPlan PlanPress(string cellName)
        {
            var keys = new List<string>(Profile.HomeKeys);
            var cursor = AppendPress(cellName, keys, Profile.OpensOn);
            return new SamsungRemoteKeyboardPlan(keys, cursor.Row, cursor.Col, null);
        }

        /// <summary>
        /// Walks <paramref name="cursor"/> to a named cell, appends the presses and the Enter, and returns
        /// where the cursor is left. Composition stays positional: each step starts from the cell the
        /// previous one finished on, which is the only way a whole query can be one blind key run.
        /// </summary>
        private (int Row, int Col) AppendPress(string cellName, List<string> keys, (int Row, int Col) cursor)
        {
            if (!_cellIndex.TryGetValue(cellName, out var target))
                return cursor;

            foreach (var key in Path(cursor, target))
                keys.Add(key);
            keys.Add(SamsungRemoteKeys.Enter);
            return target;
        }

        /// <summary>
        /// Builds the press sequence for <paramref name="text"/> assuming the cursor is on
        /// <paramref name="from"/> — for continuing a previous plan. Exposed so the grid rules can be
        /// exercised cell by cell without a TV.
        /// </summary>
        public SamsungRemoteKeyboardPlan Plan(string text, (int Row, int Col) from) => Plan(text, from, includeHome: false);

        private SamsungRemoteKeyboardPlan Plan(string text, (int Row, int Col) from, bool includeHome)
        {
            var keys = new List<string>();
            var unsupported = new List<char>();
            var cursor = from;

            foreach (var ch in text)
            {
                if (Profile.DirectKeys.TryGetValue(ch, out var direct))
                {
                    // A real key code: one press, cursor untouched.
                    keys.Add(direct);
                    continue;
                }

                if (!TryResolve(ch, out var cell))
                {
                    if (!unsupported.Contains(ch))
                        unsupported.Add(ch);
                    continue;
                }

                var target = cell;
                foreach (var key in Path(cursor, target))
                    keys.Add(key);
                keys.Add(SamsungRemoteKeys.Enter);
                cursor = target;
            }

            var missing = unsupported.Count == 0 ? null : new string(unsupported.ToArray());
            if (keys.Count == 0)
                return new SamsungRemoteKeyboardPlan(Array.Empty<string>(), from.Row, from.Col, missing);

            if (includeHome)
                keys.InsertRange(0, Profile.HomeKeys);

            return new SamsungRemoteKeyboardPlan(keys, cursor.Row, cursor.Col, missing);
        }

        /// <summary>
        /// Maps a character to the cell that types it. Digits and letters resolve case-insensitively;
        /// the space bar is an alias, since its cell's label is an icon.
        /// </summary>
        private bool TryResolve(char ch, out (int Row, int Col) cell)
        {
            cell = default;

            if (ch == ' ' && _cellIndex.TryGetValue(SpaceCell, out var space))
            {
                cell = space;
                return true;
            }

            if (_cellIndex.TryGetValue(ch.ToString(), out var exact))
            {
                cell = exact;
                return true;
            }

            if (char.IsLetter(ch))
            {
                foreach (var candidate in new[] { char.ToLowerInvariant(ch).ToString(), char.ToUpperInvariant(ch).ToString() })
                {
                    if (_cellIndex.TryGetValue(candidate, out var folded))
                    {
                        cell = folded;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Arrows that take the cursor from one cell to another. Vertical first, keeping the column (the
        /// behaviour measured on hardware), then along the target row without wrapping.
        /// </summary>
        private IEnumerable<string> Path((int Row, int Col) from, (int Row, int Col) to)
        {
            if (from == to)
                yield break;

            var cursor = from;

            while (cursor.Row != to.Row)
            {
                var key = cursor.Row < to.Row ? SamsungRemoteKeys.Down : SamsungRemoteKeys.Up;
                yield return key;
                cursor = Move(cursor, key);
            }

            while (cursor.Col != to.Col)
            {
                var key = cursor.Col < to.Col ? SamsungRemoteKeys.Right : SamsungRemoteKeys.Left;
                yield return key;
                cursor = Move(cursor, key);
            }
        }

        /// <summary>
        /// One step. Deliberately does not invent movement the app may not have: rows are entered
        /// keeping the column and clamped to the row's length, horizontal moves stay inside their row,
        /// and nothing wraps unless the profile says so.
        /// </summary>
        private (int Row, int Col) Move((int Row, int Col) cursor, string key)
        {
            switch (key)
            {
                case SamsungRemoteKeys.Left:
                case SamsungRemoteKeys.Right:
                {
                    var columns = Profile.Rows[cursor.Row].Count;
                    var col = cursor.Col + (key == SamsungRemoteKeys.Left ? -1 : 1);
                    if (col < 0 || col >= columns)
                        col = Profile.WrapHorizontal ? (col + columns) % columns : Math.Clamp(col, 0, columns - 1);
                    return (cursor.Row, col);
                }

                case SamsungRemoteKeys.Up:
                case SamsungRemoteKeys.Down:
                {
                    var row = Math.Clamp(cursor.Row + (key == SamsungRemoteKeys.Up ? -1 : 1), 0, Profile.Rows.Count - 1);
                    if (key == SamsungRemoteKeys.Down && Profile.ColumnResetRows.Contains(row))
                        return (row, 0);
                    return (row, Math.Min(cursor.Col, Profile.Rows[row].Count - 1));
                }

                default:
                    return cursor;
            }
        }
    }
}
