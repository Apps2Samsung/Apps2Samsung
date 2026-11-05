using System.Globalization;
using System.Text.RegularExpressions;
using Apps2Samsung.Cli.Commands;
using Apps2Samsung.Design;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Apps2Samsung.Cli;

/// <summary>
/// The terminal renderers (#975): the v3 look of the desktop and phone apps drawn in box
/// characters, for a result printed on a terminal. A TV is a card with its LED, the catalog and
/// the installed apps are tables (stacked lists under 72 columns, a phone in Termux), the
/// certificates are a card per level. The colours are the design tokens' dark-theme values, as
/// the TV glass in the apps: the LED colours are the same in both themes, and the accent reads
/// on a light terminal too. Every dynamic string goes through <see cref="Esc"/>: Spectre's markup
/// reads square brackets.
/// </summary>
internal static class Views
{
    public static readonly string AccentHex = Hex(DesignTokens.Colors.Accent.Dark);
    public static readonly string OkHex = Hex(DesignTokens.Colors.LedOk.Dark);
    public static readonly string WarnHex = Hex(DesignTokens.Colors.LedWarn.Dark);
    public static readonly string BadHex = Hex(DesignTokens.Colors.LedBad.Dark);
    public static readonly string MutedHex = Hex(DesignTokens.Colors.GlassMuted.Dark);
    /// <summary>Card borders, table rules and the empty part of a bar: the TV bezel.</summary>
    public static readonly string DimHex = Hex(DesignTokens.Colors.Bezel.Dark);

    /// <summary>Below this many columns the tables stack into lists: a phone terminal.</summary>
    public const int NarrowWidth = 72;

    private static string Hex(string token) => "#" + token[^6..];

    private static Color ColorOf(string hex) => new(
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

    public static string Esc(string? text) => Markup.Escape(text ?? string.Empty);

    public static string GlyphMarkup(Glyph glyph, bool unicode) => glyph switch
    {
        Glyph.Done => $"[{OkHex}]{(unicode ? "✓" : "+")}[/]",
        Glyph.Failed => $"[{BadHex}]{(unicode ? "✗" : "x")}[/]",
        _ => $"[{WarnHex}]![/]",
    };

    private static string Led(string hex, bool unicode) => $"[{hex}]{(unicode ? "●" : "*")}[/]";
    private static string Muted(string? text) => $"[{MutedHex}]{Esc(text)}[/]";
    private static string Arrow(bool unicode) => unicode ? "→" : "->";
    private static string Dot(bool unicode) => unicode ? "·" : "-";

    public static Panel Card(IRenderable content, string header, string borderHex, bool unicode) => new Panel(content)
    {
        // Spectre trims the header's whitespace, so the breathing room is a piece of border on each side: "╭── ● 192.168.1.8 ───".
        Header = new PanelHeader($"[{borderHex}]{(unicode ? "─" : "-")}[/] {header.Trim()} [{borderHex}]{(unicode ? "─" : "-")}[/]"),
        Border = unicode ? BoxBorder.Rounded : BoxBorder.Ascii,
        BorderStyle = new Style(ColorOf(borderHex)),
        Padding = new Padding(1, 0, 1, 0),
        Expand = true,
    };

    // ---- plain lines, as the simple verbs print them ----

    /// <summary>The result lines of a simple verb: the first with its glyph in bold, the rest as they are.</summary>
    public static IRenderable Lines(IReadOnlyList<string> lines, Glyph? glyph, bool unicode)
    {
        var rows = new List<IRenderable>();
        for (var i = 0; i < lines.Count; i++)
        {
            rows.Add(i == 0 && glyph is { } g
                ? new Markup($"{GlyphMarkup(g, unicode)} [bold]{Esc(lines[i])}[/]")
                : new Markup(Esc(lines[i])));
        }
        return new Rows(rows);
    }

    /// <summary>A finished install: the headline in a green card, with the ids a script would want next.</summary>
    public static IRenderable Done(IReadOnlyList<string> lines, bool unicode, params (string Key, string? Value)[] details)
    {
        var rows = new List<IRenderable> { new Markup($"{GlyphMarkup(Glyph.Done, unicode)} [bold]{Esc(lines[0])}[/]") };
        foreach (var line in lines.Skip(1))
            rows.Add(new Markup(Esc(line)));
        var known = details.Where(d => !string.IsNullOrWhiteSpace(d.Value)).ToList();
        if (known.Count > 0)
        {
            var grid = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
            foreach (var (key, value) in known)
                grid.AddRow(Muted(key), Esc(value));
            rows.Add(new Text(""));
            rows.Add(grid);
        }
        return new Panel(new Rows(rows))
        {
            Border = unicode ? BoxBorder.Rounded : BoxBorder.Ascii,
            BorderStyle = new Style(ColorOf(OkHex)),
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    // ---- TVs: scan, tv ----

    public static IRenderable Tvs(IReadOnlyList<TvState> tvs, string? localIp, IReadOnlyList<string> whenNone, bool unicode,
        string? tizen = null, string? duid = null)
    {
        var rows = new List<IRenderable>();
        if (tvs.Count == 0)
        {
            for (var i = 0; i < whenNone.Count; i++)
                rows.Add(new Markup(i == 0 ? $"[{WarnHex}]{Esc(whenNone[i])}[/]" : Muted(whenNone[i])));
            return new Rows(rows);
        }
        foreach (var tv in tvs)
            rows.Add(TvCard(tv, unicode, tizen, duid));
        var count = tvs.Count == 1 ? "1 TV" : $"{tvs.Count} TVs";
        rows.Add(new Markup(Muted(localIp is null ? count : $"{count} {Dot(unicode)} this machine is {localIp}")));
        return new Rows(rows);
    }

    /// <summary>One TV as a card: LED and address in the border, name and model, the Developer Mode state and why it is not ready.</summary>
    public static Panel TvCard(TvState tv, bool unicode, string? tizen = null, string? duid = null)
    {
        var led = tv.Ready ? OkHex : WarnHex;
        var lines = new List<string>();

        var who = new List<string>();
        if (!string.IsNullOrWhiteSpace(tv.Name)) who.Add($"[bold]{Esc(tv.Name)}[/]");
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(tv.Model)) meta.Add(tv.Model);
        if (tizen is not null) meta.Add($"Tizen {tizen}");
        if (meta.Count > 0) who.Add(Muted(string.Join($" {Dot(unicode)} ", meta)));
        if (who.Count > 0) lines.Add(string.Join("  ", who));

        var route = tv.Route == "relay" ? (tv.RelayDown ? $" {Dot(unicode)} via TV-Store relay, relay off" : $" {Dot(unicode)} via TV-Store relay") : "";
        var dev = tv.DeveloperMode ? $"developer mode on {Dot(unicode)} host {tv.DeveloperIp ?? "?"}{route}" : "developer mode off";
        lines.Add($"[{led} bold]{(tv.Ready ? "Ready" : "Not ready")}[/]  {Muted(dev)}");
        if (duid is not null)
            lines.Add(Muted($"DUID {duid}"));
        if (tv.NotReadyReason is not null)
            lines.Add($"[{WarnHex}]{Esc(tv.NotReadyReason)}[/]");

        return Card(new Markup(string.Join("\n", lines)), $" {Led(led, unicode)} [bold]{Esc(tv.Ip)}[/] ", DimHex, unicode);
    }

    // ---- catalog ----

    public static IRenderable Catalog(IReadOnlyList<CatalogCommand.Entry> apps, int width, bool unicode)
    {
        if (apps.Count == 0)
            return new Markup($"[{WarnHex}]No app matches.[/]");
        var footer = new Markup(Muted($"{apps.Count} app{(apps.Count == 1 ? "" : "s")} {Dot(unicode)} install with: apps2samsung-cli install \"<name>\" --tv <ip>"));
        if (width < NarrowWidth)
        {
            // A phone: one block per app, the detail hanging under the name.
            var rows = new List<IRenderable>();
            foreach (var e in apps)
            {
                rows.Add(new Markup($"{Tile(e)} [bold]{Esc(e.Name)}[/]{Flags(e)}"));
                var body = new List<IRenderable>();
                var meta = new List<string>();
                if (Newest(e) is { } v) meta.Add(v);
                if (TizenOf(e) is { } t) meta.Add($"Tizen {t}+");
                if (e.Downloads is { } n) meta.Add($"{n:N0} downloads");
                if (e.Category.Length > 0) meta.Add(e.Category);
                if (meta.Count > 0) body.Add(new Markup(Muted(string.Join($" {Dot(unicode)} ", meta))));
                if (e.Description.Length > 0) body.Add(new Markup(Esc(e.Description)));
                body.AddRange(FileRows(e, unicode));
                rows.Add(new Padder(new Rows(body), new Padding(2, 0, 0, 0)));
            }
            rows.Add(footer);
            return new Rows(rows);
        }

        // Category gets a column of its own only when there is room; the App column needs most of the width.
        var categoryColumn = width >= 100;
        var table = new Table
        {
            Border = TableBorder.Simple,
            BorderStyle = new Style(ColorOf(DimHex)),
            Expand = true,
        };
        table.AddColumn(new TableColumn("").NoWrap().Width(3));
        table.AddColumn(new TableColumn(Muted("App")));
        if (categoryColumn)
            table.AddColumn(new TableColumn(Muted("Category")).NoWrap());
        table.AddColumn(new TableColumn(Muted("Newest")).NoWrap());
        table.AddColumn(new TableColumn(Muted("Downloads")).RightAligned().NoWrap());
        foreach (var e in apps)
        {
            var app = new List<IRenderable> { new Markup($"[bold]{Esc(e.Name)}[/]{Flags(e)}") };
            if (e.Description.Length > 0) app.Add(new Markup(Muted(e.Description)));
            var meta = new List<string>();
            if (!categoryColumn && e.Category.Length > 0) meta.Add(e.Category);
            if (TizenOf(e) is { } t) meta.Add($"Tizen {t}+");
            if (meta.Count > 0) app.Add(new Markup(Muted(string.Join($" {Dot(unicode)} ", meta))));
            app.AddRange(FileRows(e, unicode));
            var cells = new List<IRenderable> { new Markup(Tile(e)), new Rows(app) };
            if (categoryColumn)
                cells.Add(new Markup(Muted(e.Category)));
            cells.Add(new Markup(Newest(e) is { } v ? Esc(v) : ""));
            cells.Add(new Markup(e.Downloads is { } n ? Esc(n.ToString("N0")) : ""));
            table.AddRow(cells);
        }
        return new Rows(table, footer);
    }

    private static string Tile(CatalogCommand.Entry e)
    {
        var token = AppTiles.ColorToken(e.Category, e.Name);
        return $"[white on {Hex(DesignTokens.Colors.All[token].Dark)}] {Esc(AppTiles.Letter(e.Name))} [/]";
    }

    private static string Flags(CatalogCommand.Entry e)
    {
        var flags = new List<string>();
        if (e.RequiresPartner) flags.Add("partner certificate");
        if (e.Adult) flags.Add("18+");
        if (e.Root) flags.Add("root");
        if (e.OwnSource) flags.Add("own source");
        return flags.Count == 0 ? "" : " " + Muted($"({string.Join(", ", flags)})");
    }

    private static CatalogCommand.FileInfo DefaultFile(CatalogCommand.Entry e) => e.Files.FirstOrDefault(f => f.Default) ?? e.Files[0];
    private static string? Newest(CatalogCommand.Entry e) => e.Files.Count == 0 ? e.Tag : DefaultFile(e).Version is { } v ? $"v{v}" : e.Tag;
    private static string? TizenOf(CatalogCommand.Entry e) => e.Files.Count == 0 ? null : DefaultFile(e).Tizen;

    /// <summary>The files to choose from and the values an app asks for, as the plain listing has them, when there is a choice.</summary>
    private static IEnumerable<IRenderable> FileRows(CatalogCommand.Entry e, bool unicode)
    {
        if (e.Files.Count <= 1 && !e.Files.Any(f => f.Settings is not null))
            yield break;
        foreach (var f in e.Files)
        {
            var label = f.Variant is not null ? $"{f.Variant}: {f.File}" : f.File;
            var line = $"[{AccentHex}]-[/] {Esc(label)}";
            if (f.Version is not null) line += $" [bold]v{Esc(f.Version)}[/]";
            if (f.Default) line += " " + Muted("(default)");
            if (f.Tizen is not null) line += "  " + Muted($"Tizen {f.Tizen}+");
            yield return new Markup(line);
            if (f.Settings is null)
                continue;
            var settings = new List<IRenderable>();
            foreach (var st in f.Settings)
            {
                var optional = st.Required ? "" : st.Default is null ? " (optional)" : $" (optional, default {st.Default})";
                var see = st.HelpUrl is null ? "" : $"  see {st.HelpUrl}";
                settings.Add(new Markup($"[{AccentHex}]--set[/] {Esc(st.Key)}={(unicode ? "…" : "...")}  {Esc(st.Label)}{Muted(optional + see)}"));
            }
            yield return new Padder(new Rows(settings), new Padding(2, 0, 0, 0));
        }
    }

    // ---- installed ----

    public static IRenderable InstalledApps(IReadOnlyList<InstalledCommand.Row> rows, string tv, int width, bool unicode)
    {
        if (rows.Count == 0)
            return new Markup($"[{WarnHex}]The TV lists no apps.[/]");
        var updates = rows.Where(r => r.State == "update-available").ToList();
        var footer = new Markup(Muted(updates.Count == 0
            ? $"{rows.Count} app{(rows.Count == 1 ? "" : "s")} on {tv}"
            : $"{rows.Count} apps on {tv} {Dot(unicode)} {updates.Count} update{(updates.Count == 1 ? "" : "s")}: apps2samsung-cli install \"{updates[0].CatalogApp}\" --tv {tv}"));

        if (width < NarrowWidth)
        {
            var list = new List<IRenderable>();
            foreach (var r in rows)
            {
                var lines = new List<string> { $"[bold]{Esc(r.Title)}[/]{(r.Version is null ? "" : $" v{Esc(r.Version)}")}", "  " + Muted(r.AppId) };
                if (r.CatalogApp is not null)
                    lines.Add("  " + State(r, unicode));
                list.Add(new Markup(string.Join("\n", lines)));
            }
            list.Add(footer);
            return new Rows(list);
        }

        var table = new Table
        {
            Border = TableBorder.Simple,
            BorderStyle = new Style(ColorOf(DimHex)),
            Expand = true,
        };
        table.AddColumn(new TableColumn(Muted("App")));
        table.AddColumn(new TableColumn(Muted("Installed")).NoWrap());
        table.AddColumn(new TableColumn(Muted("Catalog")));
        foreach (var r in rows)
        {
            table.AddRow(
                new Markup($"[bold]{Esc(r.Title)}[/]\n{Muted(r.AppId)}"),
                new Markup(r.Version is null ? Muted("?") : $"v{Esc(r.Version)}"),
                new Markup(r.CatalogApp is null ? "" : State(r, unicode)));
        }
        return new Rows(table, footer);
    }

    private static string State(InstalledCommand.Row r, bool unicode)
    {
        var catalog = $"{Esc(r.CatalogApp)}{(r.CatalogVersion is null ? "" : $" v{Esc(r.CatalogVersion)}")}";
        return r.State switch
        {
            "up-to-date" => $"{catalog}  [{OkHex}]up to date[/]",
            "update-available" => $"{catalog}  [{WarnHex} bold]update available[/]",
            "different" => $"{catalog}  {Muted("different build")}",
            _ => $"{catalog}  {Muted("version unknown")}",
        };
    }

    // ---- cert ----

    public static IRenderable Certificates(IReadOnlyList<Certificates.ProfileState> profiles, string? tvDuid, string? ip, string store, bool unicode)
    {
        var rows = new List<IRenderable> { new Markup(Muted($"Certificate store: {store}")) };
        foreach (var p in profiles)
        {
            string body;
            if (!p.Exists)
            {
                body = Muted($"{(unicode ? "○" : "o")} none");
            }
            else
            {
                var lines = new List<string>();
                var window = p.ValidFrom is null ? "unreadable" : $"valid {p.ValidFrom} {Arrow(unicode)} {p.ValidUntil}{(p.Usable ? "" : " (expired or unreadable)")}";
                lines.Add($"{Led(p.Usable ? OkHex : BadHex, unicode)} {Esc(window)}");
                lines.Add($"  covers {p.Duids.Count} TV{(p.Duids.Count == 1 ? "" : "s")}");
                foreach (var d in p.Duids)
                {
                    var mine = tvDuid is not null && string.Equals(d, tvDuid, StringComparison.OrdinalIgnoreCase);
                    lines.Add($"  {Muted(d)}{(mine ? $"  [{AccentHex}]{(unicode ? "←" : "<-")} {Esc(ip)}[/]" : "")}");
                }
                body = string.Join("\n", lines);
            }
            rows.Add(Card(new Markup(body), $" [bold]{Esc(p.Level)}[/] ", DimHex, unicode));
        }
        if (tvDuid is not null)
        {
            var coveredBy = profiles.Where(p => p.Usable && p.Duids.Contains(tvDuid, StringComparer.OrdinalIgnoreCase)).Select(p => p.Level).ToList();
            rows.Add(new Markup(coveredBy.Count > 0
                ? $"[{OkHex}]{Esc(ip)} is covered by {Esc(string.Join(" and ", coveredBy))}.[/] Installs need no sign-in."
                : $"[{WarnHex}]{Esc(ip)} is not covered yet.[/] The first install signs in to Samsung, or run: cert --tv {Esc(ip)} --prepare"));
        }
        return new Rows(rows);
    }

    // ---- sources ----

    public static IRenderable Sources(IReadOnlyList<string> lines, bool unicode)
    {
        if (lines.Count == 1 && lines[0].StartsWith("No sources", StringComparison.Ordinal))
            return new Markup(Muted(lines[0]));
        // "Name  [category] (partner certificate)" then the indented link lines: the name in bold, the rest muted.
        return new Rows(lines.Select(l =>
        {
            if (l.StartsWith("    ", StringComparison.Ordinal))
                return new Markup(Muted(l));
            var gap = l.IndexOf("  ", StringComparison.Ordinal);
            return new Markup(gap < 0 ? $"[bold]{Esc(l)}[/]" : $"[bold]{Esc(l[..gap])}[/]{Muted(l[gap..])}");
        }));
    }

    // ---- help ----

    private static readonly Regex HelpEntry = new(@"^  (?<name>\S.*?)(?:\s{2,}(?<desc>\S.*))?$", RegexOptions.Compiled);

    /// <summary>
    /// The help text at the terminal's width: the verbs and flags in the accent colour with their
    /// descriptions in a column beside them (under each other on a phone), section titles in bold,
    /// the prose re-wrapped. The text itself is <see cref="Cli.Help"/>, written at 100 columns.
    /// </summary>
    public static IRenderable Help(string text, int width)
    {
        var rows = new List<IRenderable>();
        var entries = new List<(string Name, string Desc)>();
        var paragraph = new List<string>();

        void FlushEntries()
        {
            if (entries.Count == 0)
                return;
            if (width < NarrowWidth)
            {
                foreach (var (name, desc) in entries)
                {
                    rows.Add(new Markup($"  [{AccentHex}]{Esc(name)}[/]"));
                    if (desc.Length > 0)
                        rows.Add(new Padder(new Markup(Esc(desc)), new Padding(6, 0, 0, 0)));
                }
            }
            else
            {
                var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(2)).AddColumn();
                foreach (var (name, desc) in entries)
                    grid.AddRow(new Markup($"[{AccentHex}]{Esc(name)}[/]"), new Markup(Esc(desc)));
                rows.Add(grid);
            }
            entries.Clear();
        }
        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            rows.Add(new Markup(Esc(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ');
            if (line.Length == 0)
            {
                FlushEntries();
                FlushParagraph();
                rows.Add(new Text(""));
            }
            else if (line.StartsWith("usage:", StringComparison.Ordinal))
            {
                rows.Add(new Markup($"{Muted("usage:")}{Esc(line[6..])}"));
            }
            else if (line.StartsWith("exit codes:", StringComparison.Ordinal))
            {
                rows.Add(new Markup($"{Muted("exit codes:")}{Esc(line[11..])}"));
            }
            else if (!char.IsWhiteSpace(line[0]) && !line.Contains(' '))
            {
                FlushParagraph();
                rows.Add(new Markup($"[bold]{Esc(line)}[/]"));
            }
            else if (!char.IsWhiteSpace(line[0]))
            {
                FlushEntries();
                paragraph.Add(line);
            }
            else if (line.StartsWith("       apps2samsung-cli ", StringComparison.Ordinal) && rows.Count > 0 && entries.Count == 0)
            {
                // The second and third usage line.
                rows.Add(new Markup(Esc(line)));
            }
            else if (HelpEntry.Match(line) is { Success: true } m && m.Groups["name"].Value.Length < 56)
            {
                FlushParagraph();
                entries.Add((m.Groups["name"].Value, m.Groups["desc"].Value));
            }
            else if (entries.Count > 0)
            {
                // A description that went on under itself: part of the entry above.
                var (name, desc) = entries[^1];
                entries[^1] = (name, (desc + " " + line.Trim()).Trim());
            }
            else
            {
                rows.Add(new Markup(Esc(line)));
            }
        }
        FlushEntries();
        FlushParagraph();
        return new Rows(rows);
    }
}
