using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Apps2Samsung.Cli;

/// <summary>
/// What a command prints. Results go to stdout: readable text by default, one JSON document with
/// <c>--json</c>. Progress and warnings go to stderr either way, so a script or an agent can read
/// stdout as the answer and still see what happened. Exit codes: 0 done, 1 failed, 2 bad usage,
/// 3 stopped at a check that <c>--yes</c> would have waved through.
/// <para>
/// On a terminal (#975) the same output is set with the v3 look: a live step line with a spinner
/// that becomes a check mark on stderr, and colours, cards and tables for the result on stdout
/// (<see cref="Rich"/>). Piped, with <c>--json</c>, <c>--plain</c>, <c>NO_COLOR</c> or
/// <c>TERM=dumb</c>, every byte is the plain text it was before.
/// </para>
/// </summary>
public sealed class Output : IDisposable
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int Stopped = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // "Installing on TV… 60%": the label and the percentage, so the bar replaces the line in place.
    private static readonly Regex Percent = new(@"^(?<label>.*?)\s*(?<pct>\d{1,3})%$", RegexOptions.Compiled);
    private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private static readonly string[] SpinnerFramesAscii = { "|", "/", "-", "\\" };

    public bool AsJson { get; }
    public bool Quiet { get; }
    /// <summary>Colours, cards and a live step line: stdout and stderr are both a terminal that takes ANSI, and nothing asked for plain text.</summary>
    public bool Rich { get; }
    /// <summary>The terminal's width in columns (80 when not a terminal), for a renderer that stacks below a threshold.</summary>
    public int Width => _stdout?.Profile.Width ?? 80;
    /// <summary>Whether the terminal draws box characters and the status glyphs; otherwise the renderers fall back to ASCII.</summary>
    public bool Unicode => _stdout?.Profile.Capabilities.Unicode ?? false;
    /// <summary>The terminal's height in rows (24 when not a terminal), for the menu's page size.</summary>
    public int Height => _stdout?.Profile.Height ?? 24;
    /// <summary>The stdout terminal, for the menu's prompts; null when not <see cref="Rich"/>.</summary>
    public IAnsiConsole? Terminal => _stdout;

    private readonly IAnsiConsole? _stdout;
    private readonly IAnsiConsole? _stderr;
    private readonly object _gate = new();
    private Step? _step;
    private Timer? _spinner;
    private int _frame;

    public Output(CliOptions options) : this(options.Json, options.Quiet, options.Plain) { }

    public Output(bool asJson, bool quiet, bool plain = false)
    {
        AsJson = asJson;
        Quiet = quiet;
        if (asJson || plain || !TerminalWantsRich())
            return;

        // Windows Terminal and conhost take UTF-8 once asked; the default code page draws boxes as '?'.
        if (OperatingSystem.IsWindows())
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* a console that cannot change encoding keeps its glyphs ASCII below */ }
        }
        var stdout = Create(Console.Out);
        var stderr = Create(Console.Error);
        if (!stdout.Profile.Capabilities.Ansi || !stderr.Profile.Capabilities.Ansi)
            return;
        _stdout = stdout;
        _stderr = stderr;
        Rich = true;
    }

    private static bool TerminalWantsRich()
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return false;
        return !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
    }

    private static IAnsiConsole Create(TextWriter writer) => AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(writer),
        Ansi = AnsiSupport.Detect,
        ColorSystem = ColorSystemSupport.Detect,
        Interactive = InteractionSupport.Yes,
    });

    /// <summary>A progress line (stderr); silenced by <c>--quiet</c>. On a terminal it is the live step line: the step before it gets its check mark.</summary>
    public void Progress(string message)
    {
        if (Quiet)
            return;
        if (!Rich)
        {
            Console.Error.WriteLine(message);
            return;
        }
        var match = Percent.Match(message);
        var label = match.Success ? match.Groups["label"].Value : message;
        int? percent = match.Success && int.TryParse(match.Groups["pct"].Value, out var p) ? Math.Clamp(p, 0, 100) : null;
        lock (_gate)
        {
            if (_step is not null && percent is not null && string.Equals(_step.Label, label, StringComparison.Ordinal))
            {
                _step.Percent = percent;
                Redraw();
                return;
            }
            CloseStep(Glyph.Done);
            _step = new Step(label) { Percent = percent };
            _stderr!.Write(RenderStep());
            _stderr.WriteLine();
            _spinner ??= new Timer(_ => Tick(), null, 100, 100);
        }
    }

    /// <summary>A line above the steps, on a terminal only: what is being installed where. Nothing when piped.</summary>
    public void Heading(string markup)
    {
        if (!Rich || Quiet)
            return;
        lock (_gate)
        {
            CloseStep(Glyph.Done);
            _stderr!.MarkupLine(markup);
        }
    }

    /// <summary>A warning (stderr), never silenced.</summary>
    public void Warn(string message)
    {
        if (!Rich)
        {
            Console.Error.WriteLine($"warning: {message}");
            return;
        }
        lock (_gate)
        {
            // The step line stays the last line: the warning goes above it.
            if (_step is not null)
                _stderr!.Cursor.MoveUp(1);
            _stderr!.Write(new ControlCode("\u001b[2K"));
            // "Title: detail", as the install checks read: the title in amber, the detail as it is.
            var colon = message.IndexOf(": ", StringComparison.Ordinal);
            var body = colon > 0 && colon < 60
                ? $"[{Views.WarnHex} bold]{Markup.Escape(message[..colon])}:[/]{Markup.Escape(message[(colon + 1)..])}"
                : Markup.Escape(message);
            _stderr.MarkupLine($"[{Views.WarnHex}]![/] {body}");
            if (_step is not null)
            {
                _stderr.Write(RenderStep());
                _stderr.WriteLine();
            }
        }
    }

    /// <summary>The command's result: the text lines, or the object as JSON. On a terminal, <paramref name="rich"/> when given, else the lines with a check mark on the first.</summary>
    public int Result(object data, IEnumerable<string> text, int exitCode = Ok, IRenderable? rich = null)
    {
        if (AsJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(data, data.GetType(), Json));
            return exitCode;
        }
        if (!Rich)
        {
            foreach (var line in text)
                Console.Out.WriteLine(line);
            return exitCode;
        }
        lock (_gate)
        {
            CloseStep(exitCode == Ok ? Glyph.Done : Glyph.Stopped);
            rich ??= Views.Lines(text.ToList(), exitCode == Ok ? Glyph.Done : exitCode == Stopped ? Glyph.Stopped : null, Unicode);
            // A single Markup is inline; Rows ends every line, the last one included.
            _stdout!.Write(rich is Markup ? new Rows(rich) : rich);
        }
        return exitCode;
    }

    /// <summary>A failure: the reason on stderr (text mode) or as the JSON result, with the exit code.</summary>
    public int Fail(string message, int exitCode = Failed, object? details = null)
    {
        if (AsJson)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message, details }, Json));
            return exitCode;
        }
        if (!Rich)
        {
            Console.Error.WriteLine($"error: {message}");
            return exitCode;
        }
        lock (_gate)
        {
            var glyph = exitCode == Stopped ? Glyph.Stopped : Glyph.Failed;
            CloseStep(glyph);
            _stderr!.MarkupLine($"{Views.GlyphMarkup(glyph, Unicode)} {Markup.Escape(message)}");
        }
        return exitCode;
    }

    /// <summary>Something to look at between the steps (the menu's screens), on a terminal only; closes the open step first.</summary>
    public void Show(IRenderable renderable)
    {
        if (!Rich)
            return;
        lock (_gate)
        {
            CloseStep(Glyph.Done);
            _stdout!.Write(renderable is Markup ? new Rows(renderable) : renderable);
        }
    }

    /// <summary>
    /// A fresh screen for the menu, on a terminal only: closes the open step and clears the
    /// terminal with the cursor at the top. ED 2 and CUP, what <c>clear</c> sends; the terminal
    /// keeps its scrollback where it has one, so a line can still be scrolled back to.
    /// </summary>
    public void Clear()
    {
        if (!Rich)
            return;
        lock (_gate)
        {
            CloseStep(Glyph.Done);
            _stdout!.Write(new ControlCode("\u001b[2J\u001b[H"));
        }
    }

    /// <summary>Closes the open step line, before a prompt takes the terminal; <paramref name="glyph"/> says how it ended.</summary>
    public void Idle(Glyph glyph = Glyph.Done)
    {
        if (!Rich)
            return;
        lock (_gate)
            CloseStep(glyph);
    }

    /// <summary>The help text: coloured on a terminal, as typed otherwise.</summary>
    public void Help(string text)
    {
        if (Rich)
            _stdout!.Write(Views.Help(text, Width));
        else
            Console.Out.WriteLine(text);
    }

    // ---- the live step line (stderr), always the last line while a step is open ----

    private sealed class Step
    {
        public Step(string label) { Label = label; }
        public string Label { get; }
        public int? Percent { get; set; }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_step is null)
                return;
            _frame++;
            Redraw();
        }
    }

    /// <summary>Rewrites the open step line in place: it is the line above the cursor.</summary>
    private void Redraw()
    {
        _stderr!.Cursor.MoveUp(1);
        _stderr.Write(RenderStep());
        _stderr.WriteLine();
    }

    private void CloseStep(Glyph glyph)
    {
        if (_step is null)
            return;
        _spinner?.Dispose();
        _spinner = null;
        _stderr!.Cursor.MoveUp(1);
        _stderr.Write(new ControlCode("\u001b[2K"));
        // The finished step, free to wrap now that no cursor arithmetic follows it: "✓ Loading the app catalog".
        var label = _step.Label.TrimEnd('…', '.', ' ');
        var done = glyph == Glyph.Done && _step.Percent is not null ? $"{label} 100%" : label;
        _stderr.MarkupLine($"{Views.GlyphMarkup(glyph, Unicode)} [{Views.MutedHex}]{Markup.Escape(done)}[/]");
        _step = null;
    }

    /// <summary>The open step as one row: glyph, label and the bar when it has a percentage, cut to the width so the cursor arithmetic holds.</summary>
    private IRenderable RenderStep()
    {
        var step = _step!;
        var frames = Unicode ? SpinnerFrames : SpinnerFramesAscii;
        var glyph = $"[{Views.AccentHex}]{frames[_frame % frames.Length]}[/]";
        var width = Math.Max(20, _stderr!.Profile.Width);
        var bar = "";
        if (step.Percent is { } pct)
        {
            var cells = width < 60 ? 12 : 20;
            var filled = (int)Math.Round(cells * pct / 100.0);
            var (on, off) = Unicode ? ("▰", "▱") : ("=", "-");
            bar = $"  [{Views.AccentHex}]{new string(on[0], filled)}[/][{Views.DimHex}]{new string(off[0], cells - filled)}[/] [bold]{pct}%[/]";
        }
        var room = width - 2 - (step.Percent is null ? 0 : (width < 60 ? 12 : 20) + 8);
        var label = step.Label.Length > room && room > 4 ? step.Label[..(room - 1)] + "…" : step.Label;
        return new Markup($"{glyph} {Markup.Escape(label)}{bar}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _spinner?.Dispose();
            _spinner = null;
            if (_step is not null)
                CloseStep(Glyph.Stopped);
        }
    }
}

/// <summary>The mark in front of a finished step or a result line.</summary>
public enum Glyph { Done, Failed, Stopped }
