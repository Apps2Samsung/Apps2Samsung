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
    /// Characters the layout cannot produce, distinct — null when everything in the string can be typed.
    /// </param>
    public sealed record SamsungRemoteKeyboardPlan(
        IReadOnlyList<string> Keys,
        int Row,
        int Col,
        string? Unsupported);

    /// <summary>
    /// Types text into an app that draws its **own** on-screen keyboard, by walking that keyboard
    /// with the d-pad (arrow presses + Enter on the letter under the cursor).
    /// <para>
    /// Why this exists next to <see cref="SamsungRemoteClient.SendTextAsync"/>: <c>SendInputString</c>
    /// reaches the TV's **system** IME, so it works in Smart Hub search, the browser and settings —
    /// but YouTube, Netflix and TizenTube paint their own keyboard inside the app, emit no
    /// <c>ms.remote.ime*</c> events, and ignore typed strings entirely. Digits are the exception and
    /// the tell: the Samsung remote has real digit keys, which those apps do handle, which is why 1-2-3
    /// lands in a YouTube search box while letters never do. Letters only exist on their grid, so the
    /// grid is what has to be pressed.
    /// </para>
    /// <para>
    /// The model is deliberately literal — rows of cell tokens, a cursor, arrows that move it, Enter
    /// that activates it — because that is exactly what the app implements. YouTube's grid is
    /// alphabetical, 7 per row, and its left/right edges wrap; other apps differ, so a layout is data,
    /// not code.
    /// </para>
    /// </summary>
    public sealed class SamsungRemoteKeyboard
    {
        /// <summary>Cell token for the space bar (a cell whose label is an icon, not a character).</summary>
        public const string SpaceCell = "SPACE";

        /// <summary>Cell token for the backspace/delete cell.</summary>
        public const string DeleteCell = "DEL";

        private readonly IReadOnlyList<IReadOnlyList<string>> _rows;
        private readonly Dictionary<string, (int Row, int Col)> _cells;

        private SamsungRemoteKeyboard(
            string id,
            IReadOnlyList<IReadOnlyList<string>> rows,
            bool wrapHorizontal,
            (int Row, int Col) start,
            IReadOnlyDictionary<char, string>? aliases,
            IReadOnlyDictionary<char, string>? directKeys)
        {
            Id = id;
            _rows = rows;
            WrapHorizontal = wrapHorizontal;
            Start = start;
            Aliases = aliases ?? new Dictionary<char, string>();
            DirectKeys = directKeys ?? new Dictionary<char, string>();

            _cells = new Dictionary<string, (int Row, int Col)>(StringComparer.Ordinal);
            for (var row = 0; row < rows.Count; row++)
            {
                for (var col = 0; col < rows[row].Count; col++)
                    _cells[rows[row][col]] = (row, col);
            }
        }

        /// <summary>Layout name, for diagnostics.</summary>
        public string Id { get; }

        /// <summary>The cells, top row first. Each cell is one character or a cell token.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Rows => _rows;

        /// <summary>Whether the app's cursor loops round the ends of a row (YouTube does).</summary>
        public bool WrapHorizontal { get; }

        /// <summary>Where the cursor sits when the keyboard opens — where a plan starts.</summary>
        public (int Row, int Col) Start { get; }

        /// <summary>Characters that need a different cell than their own glyph (space → the space bar).</summary>
        public IReadOnlyDictionary<char, string> Aliases { get; }

        /// <summary>
        /// Characters that are typed with a real key code instead of a grid cell. Digits are the case
        /// that matters: these apps keep numbers on a separate keyboard screen, yet they do handle the
        /// Samsung remote's own digit keys — which is exactly why 1-2-3 lands in a YouTube search box
        /// while letters never do.
        /// </summary>
        public IReadOnlyDictionary<char, string> DirectKeys { get; }

        /// <summary>
        /// YouTube's search keyboard: A-Z in alphabetical order, 7 per row, wrap-around left/right,
        /// with the space bar and delete sharing the last row. Numbers never need the grid — they go
        /// out as the remote's digit keys.
        /// </summary>
        public static SamsungRemoteKeyboard YouTubeSearch { get; } = new(
            "youtube-search",
            new[]
            {
                new[] { "a", "b", "c", "d", "e", "f", "g" },
                new[] { "h", "i", "j", "k", "l", "m", "n" },
                new[] { "o", "p", "q", "r", "s", "t", "u" },
                new[] { "v", "w", "x", "y", "z", SpaceCell, DeleteCell },
            },
            wrapHorizontal: true,
            start: (0, 0),
            aliases: new Dictionary<char, string> { [' '] = SpaceCell },
            directKeys: DigitKeys());

        private static IReadOnlyDictionary<char, string> DigitKeys()
        {
            var keys = new Dictionary<char, string>();
            for (var digit = 0; digit <= 9; digit++)
                keys[(char)('0' + digit)] = SamsungRemoteKeys.Digit(digit);
            return keys;
        }

        /// <summary>
        /// Builds the press sequence for <paramref name="text"/>, starting from the layout's
        /// <see cref="Start"/> cell. The caller is responsible for the cursor actually being there —
        /// <see cref="PlanFromUnknownCursor"/> parks it first when it might not be.
        /// </summary>
        public SamsungRemoteKeyboardPlan Plan(string text) => Plan(text, Start);

        /// <summary>
        /// Builds the press sequence for <paramref name="text"/> without knowing where the app's
        /// cursor is. It parks the cursor on <see cref="Start"/> first (one LEFT per column, one UP
        /// per row always lands on the top-left cell, wrap or not), then types.
        /// </summary>
        public SamsungRemoteKeyboardPlan PlanFromUnknownCursor(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new SamsungRemoteKeyboardPlan(Array.Empty<string>(), Start.Row, Start.Col, null);

            var park = new List<string>();
            park.AddRange(Enumerable.Repeat(SamsungRemoteKeys.Left, _rows.Max(row => row.Count) + 1));
            park.AddRange(Enumerable.Repeat(SamsungRemoteKeys.Up, _rows.Count + 1));

            var plan = Plan(text);
            var keys = park.Concat(plan.Keys).ToList();
            return plan with { Keys = keys };
        }

        private SamsungRemoteKeyboardPlan Plan(string text, (int Row, int Col) from)
        {
            var keys = new List<string>();
            var unsupported = new List<char>();
            var cursor = from;

            foreach (var ch in text)
            {
                // Digits (and anything else the remote has a real key for) never touch the grid.
                if (DirectKeys.TryGetValue(ch, out var direct))
                {
                    keys.Add(direct);
                    continue;
                }

                if (!TryResolve(ch, out var cell))
                {
                    if (!unsupported.Contains(ch))
                        unsupported.Add(ch);
                    continue;
                }

                var target = _cells[cell];
                foreach (var key in Path(cursor, target))
                    keys.Add(key);
                keys.Add(SamsungRemoteKeys.Enter);
                cursor = target;
            }

            var missing = unsupported.Count == 0 ? null : new string(unsupported.ToArray());
            return new SamsungRemoteKeyboardPlan(keys, cursor.Row, cursor.Col, missing);
        }

        /// <summary>
        /// Maps a character to the cell that types it. Letters resolve case-insensitively — the apps
        /// uppercase what they display anyway and their grids only carry one of the two.
        /// </summary>
        private bool TryResolve(char ch, out string cell)
        {
            if (Aliases.TryGetValue(ch, out var mapped))
            {
                cell = mapped;
                return true;
            }

            cell = ch.ToString();
            if (_cells.ContainsKey(cell))
                return true;

            if (char.IsLetter(ch))
            {
                cell = char.ToLowerInvariant(ch).ToString();
                if (_cells.ContainsKey(cell))
                    return true;
                cell = char.ToUpperInvariant(ch).ToString();
                return _cells.ContainsKey(cell);
            }

            return false;
        }

        /// <summary>
        /// Arrows that take the cursor from one cell to another. Vertical first — moving down inside
        /// the column then sliding along the target row keeps the walk short and, unlike a horizontal
        /// first walk, cannot be pushed off a shorter row's end.
        /// </summary>
        private IEnumerable<string> Path((int Row, int Col) from, (int Row, int Col) to)
        {
            var cursor = from;

            while (cursor.Row != to.Row)
            {
                var key = cursor.Row < to.Row ? SamsungRemoteKeys.Down : SamsungRemoteKeys.Up;
                yield return key;
                cursor = Move(cursor, key);
            }

            var columns = _rows[cursor.Row].Count;
            while (cursor.Col != to.Col)
            {
                var key = SingleKey(cursor.Col, to.Col, columns);
                yield return key;
                cursor = Move(cursor, key);
            }
        }

        private string SingleKey(int fromCol, int toCol, int columns)
        {
            if (!WrapHorizontal)
                return toCol > fromCol ? SamsungRemoteKeys.Right : SamsungRemoteKeys.Left;

            var forward = ((toCol - fromCol) % columns + columns) % columns;
            var backward = ((fromCol - toCol) % columns + columns) % columns;
            return forward <= backward ? SamsungRemoteKeys.Right : SamsungRemoteKeys.Left;
        }

        private (int Row, int Col) Move((int Row, int Col) cursor, string key)
        {
            switch (key)
            {
                case SamsungRemoteKeys.Left:
                case SamsungRemoteKeys.Right:
                {
                    var columns = _rows[cursor.Row].Count;
                    var col = cursor.Col + (key == SamsungRemoteKeys.Left ? -1 : 1);
                    if (col < 0 || col >= columns)
                        col = WrapHorizontal ? (col + columns) % columns : Math.Clamp(col, 0, columns - 1);
                    return (cursor.Row, col);
                }

                case SamsungRemoteKeys.Up:
                case SamsungRemoteKeys.Down:
                {
                    var row = Math.Clamp(cursor.Row + (key == SamsungRemoteKeys.Up ? -1 : 1), 0, _rows.Count - 1);
                    // Rows can be shorter than each other (YouTube's last row), so keep the column
                    // inside the row we land on.
                    return (row, Math.Min(cursor.Col, _rows[row].Count - 1));
                }

                default:
                    return cursor;
            }
        }
    }
}
