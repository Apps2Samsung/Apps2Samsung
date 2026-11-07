using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Rewrites Unicode property escapes (<c>\p{L}</c>, <c>\P{White_Space}</c>, ES2018) in regex
    /// literals into the code point ranges they stand for, as Babel's regexpu does. esbuild leaves
    /// them alone at <c>--target=es2015</c>, and a TV older than Chrome 64 then cannot parse the whole
    /// file. Jellyfin Enhanced's Media Tags redesign (n00bcodr/Jellyfin-Enhanced@2e0f925) brought the
    /// first ones into a supported plugin.
    /// <para>
    /// The regex keeps its <c>u</c> flag (ES2015), so astral code points are written as
    /// <c>\u{1F600}</c> and the match is the same as with the escape. General categories (<c>L</c>,
    /// <c>Lu</c>, <c>Letter</c>, <c>gc=Nd</c>, ...) come from .NET's Unicode data; <c>White_Space</c>,
    /// <c>ASCII</c>, <c>ASCII_Hex_Digit</c> and <c>Any</c> are written out. A regex with any other
    /// property (a <c>Script=</c>, say) is left as it is.
    /// </para>
    /// </summary>
    public static class RegexPropertyEscapes
    {
        private const int MaxCodePoint = 0x10FFFF;

        private static readonly ConcurrentDictionary<string, IReadOnlyList<(int From, int To)>?> Cache = new(StringComparer.Ordinal);
        private static readonly Lazy<Dictionary<UnicodeCategory, List<(int From, int To)>>> Categories = new(BuildCategories);

        /// <summary>Returns <paramref name="js"/> with every property escape in a <c>u</c> regex literal replaced by ranges; unchanged when there is none.</summary>
        public static string Lower(string js)
        {
            if (string.IsNullOrEmpty(js) || (js.IndexOf("\\p{", StringComparison.Ordinal) < 0 && js.IndexOf("\\P{", StringComparison.Ordinal) < 0))
                return js;

            var sb = new StringBuilder(js.Length);
            var copied = 0;
            var search = 0;
            while (search < js.Length)
            {
                var hit = NextEscape(js, search);
                if (hit < 0)
                    break;
                if (!TryFindRegex(js, hit, out var start, out var end, out var flags) || flags.IndexOf('u') < 0)
                {
                    search = hit + 3;
                    continue;
                }
                var body = js.Substring(start + 1, end - start - 1);
                var lowered = LowerBody(body);
                if (lowered is not null)
                {
                    sb.Append(js, copied, start + 1 - copied);
                    sb.Append(lowered);
                    copied = end;
                }
                search = end + 1;
            }
            if (copied == 0)
                return js;
            sb.Append(js, copied, js.Length - copied);
            return sb.ToString();
        }

        // The next "\p{" or "\P{" whose backslash is not itself escaped.
        private static int NextEscape(string js, int from)
        {
            for (var i = from; i < js.Length - 2; i++)
            {
                if (js[i] != '\\' || (js[i + 1] != 'p' && js[i + 1] != 'P') || js[i + 2] != '{')
                    continue;
                var slashes = 0;
                for (var j = i - 1; j >= 0 && js[j] == '\\'; j--)
                    slashes++;
                if (slashes % 2 == 0)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// The regex literal around <paramref name="at"/>: the slashes and the flags. A regex never
        /// spans lines, so its opening slash is on the same line; the candidates are tried from the
        /// line's start, because a slash that is not a regex start (a division, a slash inside a
        /// class) stops parsing at the next real boundary, before it reaches the escape.
        /// </summary>
        private static bool TryFindRegex(string js, int at, out int start, out int end, out string flags)
        {
            start = end = -1;
            flags = string.Empty;
            var lineStart = js.LastIndexOf('\n', at) + 1;
            for (var s = lineStart; s < at; s++)
            {
                if (js[s] != '/')
                    continue;
                var e = ParseBody(js, s);
                if (e <= at)
                    continue;
                var f = e + 1;
                while (f < js.Length && char.IsAsciiLetterLower(js[f]))
                    f++;
                start = s;
                end = e;
                flags = js.Substring(e + 1, f - e - 1);
                return true;
            }
            return false;
        }

        // From an opening slash: the index of the closing slash, or -1 at a line end or an empty body.
        private static int ParseBody(string js, int open)
        {
            var inClass = false;
            for (var i = open + 1; i < js.Length; i++)
            {
                var c = js[i];
                if (c is '\n' or '\r')
                    return -1;
                if (c == '\\')
                {
                    i++;
                    continue;
                }
                if (c == '[')
                    inClass = true;
                else if (c == ']')
                    inClass = false;
                else if (c == '/' && !inClass)
                    return i == open + 1 ? -1 : i;
            }
            return -1;
        }

        /// <summary>The body with its property escapes replaced, or null when one of them is not known here (the regex stays as it is).</summary>
        internal static string? LowerBody(string body)
        {
            var sb = new StringBuilder(body.Length);
            var inClass = false;
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    var next = body[i + 1];
                    if ((next == 'p' || next == 'P') && i + 2 < body.Length && body[i + 2] == '{')
                    {
                        var close = body.IndexOf('}', i + 3);
                        if (close < 0)
                            return null;
                        var ranges = Resolve(body.Substring(i + 3, close - i - 3));
                        if (ranges is null)
                            return null;
                        if (next == 'P')
                            ranges = Complement(ranges);
                        var set = Write(ranges);
                        sb.Append(inClass ? set : $"[{set}]");
                        i = close;
                        continue;
                    }
                    sb.Append(c).Append(next);
                    i++;
                    continue;
                }
                if (c == '[')
                    inClass = true;
                else if (c == ']')
                    inClass = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static IReadOnlyList<(int From, int To)>? Resolve(string name) => Cache.GetOrAdd(name, static n =>
        {
            var value = n;
            var eq = n.IndexOf('=');
            if (eq >= 0)
            {
                var key = n[..eq];
                if (key is not ("gc" or "General_Category"))
                    return null;
                value = n[(eq + 1)..];
            }
            else
            {
                switch (n)
                {
                    case "Any": return new[] { (0, MaxCodePoint) };
                    case "ASCII": return new[] { (0, 0x7F) };
                    case "ASCII_Hex_Digit" or "AHex": return new[] { (0x30, 0x39), (0x41, 0x46), (0x61, 0x66) };
                    case "White_Space" or "space":
                        return new[]
                        {
                            (0x09, 0x0D), (0x20, 0x20), (0x85, 0x85), (0xA0, 0xA0), (0x1680, 0x1680), (0x2000, 0x200A),
                            (0x2028, 0x2029), (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000),
                        };
                }
            }
            var categories = GeneralCategory(value);
            return categories is null ? null : Union(categories);
        });

        private static UnicodeCategory[]? GeneralCategory(string name) => name switch
        {
            "L" or "Letter" => new[] { UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter, UnicodeCategory.ModifierLetter, UnicodeCategory.OtherLetter },
            "LC" or "Cased_Letter" => new[] { UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.TitlecaseLetter },
            "Lu" or "Uppercase_Letter" => new[] { UnicodeCategory.UppercaseLetter },
            "Ll" or "Lowercase_Letter" => new[] { UnicodeCategory.LowercaseLetter },
            "Lt" or "Titlecase_Letter" => new[] { UnicodeCategory.TitlecaseLetter },
            "Lm" or "Modifier_Letter" => new[] { UnicodeCategory.ModifierLetter },
            "Lo" or "Other_Letter" => new[] { UnicodeCategory.OtherLetter },
            "M" or "Mark" or "Combining_Mark" => new[] { UnicodeCategory.NonSpacingMark, UnicodeCategory.SpacingCombiningMark, UnicodeCategory.EnclosingMark },
            "Mn" or "Nonspacing_Mark" => new[] { UnicodeCategory.NonSpacingMark },
            "Mc" or "Spacing_Mark" => new[] { UnicodeCategory.SpacingCombiningMark },
            "Me" or "Enclosing_Mark" => new[] { UnicodeCategory.EnclosingMark },
            "N" or "Number" => new[] { UnicodeCategory.DecimalDigitNumber, UnicodeCategory.LetterNumber, UnicodeCategory.OtherNumber },
            "Nd" or "Decimal_Number" or "digit" => new[] { UnicodeCategory.DecimalDigitNumber },
            "Nl" or "Letter_Number" => new[] { UnicodeCategory.LetterNumber },
            "No" or "Other_Number" => new[] { UnicodeCategory.OtherNumber },
            "P" or "Punctuation" or "punct" => new[]
            {
                UnicodeCategory.ConnectorPunctuation, UnicodeCategory.DashPunctuation, UnicodeCategory.OpenPunctuation, UnicodeCategory.ClosePunctuation,
                UnicodeCategory.InitialQuotePunctuation, UnicodeCategory.FinalQuotePunctuation, UnicodeCategory.OtherPunctuation,
            },
            "Pc" or "Connector_Punctuation" => new[] { UnicodeCategory.ConnectorPunctuation },
            "Pd" or "Dash_Punctuation" => new[] { UnicodeCategory.DashPunctuation },
            "Ps" or "Open_Punctuation" => new[] { UnicodeCategory.OpenPunctuation },
            "Pe" or "Close_Punctuation" => new[] { UnicodeCategory.ClosePunctuation },
            "Pi" or "Initial_Punctuation" => new[] { UnicodeCategory.InitialQuotePunctuation },
            "Pf" or "Final_Punctuation" => new[] { UnicodeCategory.FinalQuotePunctuation },
            "Po" or "Other_Punctuation" => new[] { UnicodeCategory.OtherPunctuation },
            "S" or "Symbol" => new[] { UnicodeCategory.MathSymbol, UnicodeCategory.CurrencySymbol, UnicodeCategory.ModifierSymbol, UnicodeCategory.OtherSymbol },
            "Sm" or "Math_Symbol" => new[] { UnicodeCategory.MathSymbol },
            "Sc" or "Currency_Symbol" => new[] { UnicodeCategory.CurrencySymbol },
            "Sk" or "Modifier_Symbol" => new[] { UnicodeCategory.ModifierSymbol },
            "So" or "Other_Symbol" => new[] { UnicodeCategory.OtherSymbol },
            "Z" or "Separator" => new[] { UnicodeCategory.SpaceSeparator, UnicodeCategory.LineSeparator, UnicodeCategory.ParagraphSeparator },
            "Zs" or "Space_Separator" => new[] { UnicodeCategory.SpaceSeparator },
            "Zl" or "Line_Separator" => new[] { UnicodeCategory.LineSeparator },
            "Zp" or "Paragraph_Separator" => new[] { UnicodeCategory.ParagraphSeparator },
            "C" or "Other" => new[] { UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned },
            "Cc" or "Control" or "cntrl" => new[] { UnicodeCategory.Control },
            "Cf" or "Format" => new[] { UnicodeCategory.Format },
            "Cs" or "Surrogate" => new[] { UnicodeCategory.Surrogate },
            "Co" or "Private_Use" => new[] { UnicodeCategory.PrivateUse },
            "Cn" or "Unassigned" => new[] { UnicodeCategory.OtherNotAssigned },
            _ => null,
        };

        private static Dictionary<UnicodeCategory, List<(int From, int To)>> BuildCategories()
        {
            var map = new Dictionary<UnicodeCategory, List<(int From, int To)>>();
            var runStart = 0;
            var runCategory = CharUnicodeInfo.GetUnicodeCategory(0);
            for (var cp = 1; cp <= MaxCodePoint + 1; cp++)
            {
                var category = cp <= MaxCodePoint ? CharUnicodeInfo.GetUnicodeCategory(cp) : (UnicodeCategory)(-1);
                if (category == runCategory)
                    continue;
                if (!map.TryGetValue(runCategory, out var list))
                    map[runCategory] = list = new List<(int From, int To)>();
                list.Add((runStart, cp - 1));
                runStart = cp;
                runCategory = category;
            }
            return map;
        }

        private static IReadOnlyList<(int From, int To)> Union(UnicodeCategory[] categories)
        {
            var all = new List<(int From, int To)>();
            foreach (var category in categories)
            {
                if (Categories.Value.TryGetValue(category, out var ranges))
                    all.AddRange(ranges);
            }
            all.Sort((a, b) => a.From.CompareTo(b.From));
            var merged = new List<(int From, int To)>();
            foreach (var r in all)
            {
                if (merged.Count > 0 && r.From <= merged[^1].To + 1)
                    merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
                else
                    merged.Add(r);
            }
            return merged;
        }

        private static IReadOnlyList<(int From, int To)> Complement(IReadOnlyList<(int From, int To)> ranges)
        {
            var result = new List<(int From, int To)>();
            var next = 0;
            foreach (var (from, to) in ranges)
            {
                if (from > next)
                    result.Add((next, from - 1));
                next = to + 1;
            }
            if (next <= MaxCodePoint)
                result.Add((next, MaxCodePoint));
            return result;
        }

        // Class contents, every code point escaped, so no ']', '-', '\' or '/' can end up bare.
        private static string Write(IReadOnlyList<(int From, int To)> ranges)
        {
            var sb = new StringBuilder(ranges.Count * 14);
            foreach (var (from, to) in ranges)
            {
                sb.Append(Escape(from));
                if (to != from)
                    sb.Append('-').Append(Escape(to));
            }
            return sb.ToString();
        }

        // Surrogates in braces too: in a u regex "😀" is read as one pair, never as two ends.
        private static string Escape(int cp) => cp <= 0xFFFF && cp is < 0xD800 or > 0xDFFF ? $"\\u{cp:X4}" : $"\\u{{{cp:X}}}";
    }
}
