using System.Collections.Generic;

namespace Apps2Samsung.Design
{
    /// <summary>A colour token: one value for the light theme and one for the dark theme, as
    /// <c>#RRGGBB</c> or <c>#AARRGGBB</c>, which both Avalonia's <c>Color.Parse</c> and MAUI's
    /// <c>Color.FromArgb</c> read.</summary>
    public readonly record struct ColorToken(string Light, string Dark);

    /// <summary>
    /// The single source of truth for the v3 design language (issue #755): colours, radii, sizes and
    /// the type ramp that the desktop head (Avalonia) and the mobile head (MAUI) both draw from. No
    /// XAML can be shared between the two, so each head builds its resource dictionary from these
    /// values in code at startup (<c>DesignTheme</c> in each head) and its views reference the keys:
    /// <c>{DynamicResource AccentBrush}</c>, <c>{DynamicResource InkColor}</c>, and so on. A hex
    /// value in a view is a bug; the value belongs here.
    /// </summary>
    public static class DesignTokens
    {
        /// <summary>
        /// Colours, light / dark. The resource key for a token is its name plus <c>Color</c> or
        /// <c>Brush</c> (<c>AccentColor</c>, <c>AccentBrush</c>), identical in both heads.
        /// </summary>
        public static class Colors
        {
            /// <summary>Page background: iOS grouped-table grey / true black.</summary>
            public static readonly ColorToken Bg = new("#F2F2F7", "#000000");
            /// <summary>Grouped list cards, notices, remote keys.</summary>
            public static readonly ColorToken Surface = new("#FFFFFF", "#1C1C1E");
            /// <summary>Second-level fills inside a card, skeleton shimmer.</summary>
            public static readonly ColorToken Surface2 = new("#F7F7FA", "#2C2C2E");
            /// <summary>Text.</summary>
            public static readonly ColorToken Ink = new("#1C1C1E", "#F2F2F7");
            /// <summary>Secondary text, group titles, tab labels.</summary>
            public static readonly ColorToken Muted = new("#6C6C70", "#98989F");
            /// <summary>Hairline separators and card strokes (rgba(60,60,67,.18) / rgba(84,84,88,.55)).</summary>
            public static readonly ColorToken Hair = new("#2E3C3C43", "#8C545458");
            /// <summary>Inputs, segmented-control track, "plain" buttons, toggle track
            /// (rgba(118,118,128,.12) / .24).</summary>
            public static readonly ColorToken Fill = new("#1F767680", "#3D767680");

            /// <summary>The brand teal / emerald from the website: primary buttons, active tab,
            /// links, focus ring.</summary>
            public static readonly ColorToken Accent = new("#10B981", "#2DD4BF");
            /// <summary>Accent under the pointer or pressed.</summary>
            public static readonly ColorToken AccentHover = new("#059669", "#5EEAD4");
            /// <summary>Accent at 12 % / 18 %: "soft" buttons, step numbers.</summary>
            public static readonly ColorToken AccentSoft = new("#1F10B981", "#2E2DD4BF");
            /// <summary>Text and icons on an accent surface.</summary>
            public static readonly ColorToken OnAccent = new("#FFFFFF", "#042F2E");
            /// <summary>Secondary text on an accent surface (OnAccent at 75 %).</summary>
            public static readonly ColorToken OnAccentMuted = new("#BFFFFFFF", "#BF042F2E");

            /// <summary>Status: ready, done.</summary>
            public static readonly ColorToken Ok = new("#1F9D55", "#30C46E");
            public static readonly ColorToken OkSoft = new("#1F1F9D55", "#2E30C46E");
            /// <summary>Status: found but not ready, caution notices.</summary>
            public static readonly ColorToken Warn = new("#C77700", "#F0A020");
            public static readonly ColorToken WarnSoft = new("#1FC77700", "#2EF0A020");
            /// <summary>Status: unreachable, errors, the danger button.</summary>
            public static readonly ColorToken Bad = new("#D93025", "#FF5C50");
            public static readonly ColorToken BadSoft = new("#1FD93025", "#2EFF5C50");

            /// <summary>The TV "screen" (and anything console-like): same in both themes.</summary>
            public static readonly ColorToken Glass = new("#0D1B2A", "#0D1B2A");
            public static readonly ColorToken GlassInk = new("#F4F7FB", "#F4F7FB");
            public static readonly ColorToken GlassMuted = new("#9FB0C6", "#9FB0C6");
            /// <summary>The TV frame and stand.</summary>
            public static readonly ColorToken Bezel = new("#2A2D33", "#3A3D44");
            /// <summary>The diagonal reflection on the glass: white at 10 %, fading to 3 %.</summary>
            public static readonly ColorToken GlassSheen = new("#1AFFFFFF", "#1AFFFFFF");
            public static readonly ColorToken GlassSheenFade = new("#08FFFFFF", "#08FFFFFF");
            /// <summary>The progress track and chip-button border on the glass (glass-ink at 14 % / 40 %).</summary>
            public static readonly ColorToken GlassTrack = new("#24F4F7FB", "#24F4F7FB");
            public static readonly ColorToken GlassChip = new("#66F4F7FB", "#66F4F7FB");
            /// <summary>The status LED on the glass. The glass is the same in both themes, so these
            /// are the dark-theme status colours in both: green = ready, amber = found but not
            /// ready, red = unreachable, accent = working (pulsing).</summary>
            public static readonly ColorToken LedOk = new("#30C46E", "#30C46E");
            public static readonly ColorToken LedWarn = new("#F0A020", "#F0A020");
            public static readonly ColorToken LedBad = new("#FF5C50", "#FF5C50");
            public static readonly ColorToken LedBusy = new("#2DD4BF", "#2DD4BF");

            /// <summary>Drop shadows under cards and dialogs. Same in both themes so a view can read
            /// it with a StaticResource where a DynamicResource can't reach (an effect, a gradient).</summary>
            public static readonly ColorToken Shadow = new("#33000000", "#33000000");

            /// <summary>Every token by name, for the heads to turn into resources.</summary>
            public static IReadOnlyDictionary<string, ColorToken> All { get; } = new Dictionary<string, ColorToken>
            {
                [nameof(Bg)] = Bg,
                [nameof(Surface)] = Surface,
                [nameof(Surface2)] = Surface2,
                [nameof(Ink)] = Ink,
                [nameof(Muted)] = Muted,
                [nameof(Hair)] = Hair,
                [nameof(Fill)] = Fill,
                [nameof(Accent)] = Accent,
                [nameof(AccentHover)] = AccentHover,
                [nameof(AccentSoft)] = AccentSoft,
                [nameof(OnAccent)] = OnAccent,
                [nameof(OnAccentMuted)] = OnAccentMuted,
                [nameof(Ok)] = Ok,
                [nameof(OkSoft)] = OkSoft,
                [nameof(Warn)] = Warn,
                [nameof(WarnSoft)] = WarnSoft,
                [nameof(Bad)] = Bad,
                [nameof(BadSoft)] = BadSoft,
                [nameof(Glass)] = Glass,
                [nameof(GlassInk)] = GlassInk,
                [nameof(GlassMuted)] = GlassMuted,
                [nameof(Bezel)] = Bezel,
                [nameof(GlassSheen)] = GlassSheen,
                [nameof(GlassSheenFade)] = GlassSheenFade,
                [nameof(GlassTrack)] = GlassTrack,
                [nameof(GlassChip)] = GlassChip,
                [nameof(LedOk)] = LedOk,
                [nameof(LedWarn)] = LedWarn,
                [nameof(LedBad)] = LedBad,
                [nameof(LedBusy)] = LedBusy,
                [nameof(Shadow)] = Shadow,
            };
        }

        /// <summary>Corner radii, in device-independent pixels. Resource key: <c>Radius</c> + name.</summary>
        public static class Radius
        {
            public const double Card = 12;
            public const double Button = 12;
            public const double Input = 10;
            public const double Tile = 11;
            public const double Segment = 9;
            public const double Sheet = 16;
            public const double Bezel = 18;
            public const double Screen = 12;
            public const double Chip = 999;

            public static IReadOnlyDictionary<string, double> All { get; } = new Dictionary<string, double>
            {
                [nameof(Card)] = Card, [nameof(Button)] = Button, [nameof(Input)] = Input,
                [nameof(Tile)] = Tile, [nameof(Segment)] = Segment, [nameof(Sheet)] = Sheet,
                [nameof(Bezel)] = Bezel, [nameof(Screen)] = Screen, [nameof(Chip)] = Chip,
            };
        }

        /// <summary>Spacing and sizes, in device-independent pixels. Resource key: <c>Size</c> + name.</summary>
        public static class Size
        {
            public const double Gutter = 16;
            public const double ContentMaxWidth = 560;
            public const double RowMinHeight = 52;
            public const double ButtonMinHeight = 50;
            public const double Tile = 44;
            public const double StatusDot = 10;
            public const double ScreenMinHeight = 150;

            public static IReadOnlyDictionary<string, double> All { get; } = new Dictionary<string, double>
            {
                [nameof(Gutter)] = Gutter, [nameof(ContentMaxWidth)] = ContentMaxWidth,
                [nameof(RowMinHeight)] = RowMinHeight, [nameof(ButtonMinHeight)] = ButtonMinHeight,
                [nameof(Tile)] = Tile, [nameof(StatusDot)] = StatusDot,
                [nameof(ScreenMinHeight)] = ScreenMinHeight,
            };
        }

        /// <summary>The type ramp, in points. Resource key: <c>Font</c> + name.</summary>
        public static class Type
        {
            public const double Body = 17;
            public const double PageTitle = 32;
            public const double TvName = 23;
            public const double SheetTitle = 20;
            public const double IpDisplay = 34;
            public const double GroupTitle = 15;
            public const double Footnote = 13;
            public const double TabLabel = 11;

            public static IReadOnlyDictionary<string, double> All { get; } = new Dictionary<string, double>
            {
                [nameof(Body)] = Body, [nameof(PageTitle)] = PageTitle, [nameof(TvName)] = TvName,
                [nameof(SheetTitle)] = SheetTitle, [nameof(IpDisplay)] = IpDisplay,
                [nameof(GroupTitle)] = GroupTitle, [nameof(Footnote)] = Footnote,
                [nameof(TabLabel)] = TabLabel,
            };
        }
    }
}
