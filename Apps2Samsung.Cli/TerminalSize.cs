using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace Apps2Samsung.Cli;

/// <summary>
/// The terminal's size as the CLI believes it (#975). .NET asks the terminal with an ioctl and falls
/// back to the terminfo entry (80 by 24 for xterm) when that fails; on a phone in Termux the fallback
/// is what came back, while the terminal was narrower. Spectre pads every line of a prompt to the
/// width it believes, so each line spilled onto a second one and the list piled up copies of itself
/// on every key. Three sources, in order: <c>COLUMNS</c>/<c>LINES</c> in the environment (an escape
/// hatch for the user), <c>stty size</c> when it disagrees with .NET (the terminal's own answer),
/// and .NET's own value. Whatever the source, one column stays unused, for a terminal that wraps as
/// soon as the last column is filled.
/// </summary>
internal static class TerminalSize
{
    /// <summary>The columns Spectre may use at the right edge: none. One column of margin on every terminal.</summary>
    public const int Margin = 1;

    private static readonly Lazy<(int Cols, int Rows)?> s_pinned = new(Measure);

    /// <summary>A size that overrides .NET's, when another source knows better; null to follow .NET's (resizes included).</summary>
    public static (int Cols, int Rows)? Pinned => s_pinned.Value;

    /// <summary>The output of <c>stty size</c> ("rows cols"), or null when it is not that.</summary>
    public static (int Cols, int Rows)? Parse(string? sttyOutput)
    {
        if (string.IsNullOrWhiteSpace(sttyOutput))
            return null;
        var parts = sttyOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var rows) || !int.TryParse(parts[1], out var cols))
            return null;
        return cols > 0 && rows > 0 ? (cols, rows) : null;
    }

    /// <summary>
    /// Which size to pin, given the three sources: the environment wins, then <c>stty</c> when it
    /// disagrees with .NET (.NET's fallback is the suspect), else nothing is pinned.
    /// </summary>
    public static (int Cols, int Rows)? Resolve((int Cols, int Rows)? environment, (int Cols, int Rows)? stty, (int Cols, int Rows) dotnet)
    {
        if (environment is { } env)
            return env;
        if (stty is { } s && s != dotnet)
            return s;
        return null;
    }

    private static (int Cols, int Rows)? Measure()
    {
        try
        {
            (int Cols, int Rows) dotnet = (Console.WindowWidth, Console.WindowHeight);
            return Resolve(FromEnvironment(), FromStty(), dotnet);
        }
        catch
        {
            return null;
        }
    }

    private static (int Cols, int Rows)? FromEnvironment()
    {
        var cols = Environment.GetEnvironmentVariable("COLUMNS");
        var rows = Environment.GetEnvironmentVariable("LINES");
        if (int.TryParse(cols, out var c) && c > 0)
            return (c, int.TryParse(rows, out var r) && r > 0 ? r : Console.WindowHeight);
        return null;
    }

    /// <summary>Asks <c>stty</c>, which reads the terminal on the inherited stdin; Unix only, a second at most, null when it is not there or says nothing.</summary>
    private static (int Cols, int Rows)? FromStty()
    {
        if (OperatingSystem.IsWindows() || Console.IsInputRedirected)
            return null;
        try
        {
            using var stty = Process.Start(new ProcessStartInfo("stty", "size")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (stty is null)
                return null;
            var output = stty.StandardOutput.ReadToEnd();
            if (!stty.WaitForExit(1000))
            {
                stty.Kill();
                return null;
            }
            return stty.ExitCode == 0 ? Parse(output) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Spectre's console output with the size from <see cref="TerminalSize"/>: the pinned one when there is one, .NET's otherwise, and the margin off the width either way.</summary>
internal sealed class TerminalOutput : IAnsiConsoleOutput
{
    private readonly AnsiConsoleOutput _inner;

    public TerminalOutput(TextWriter writer) => _inner = new AnsiConsoleOutput(writer);

    public TextWriter Writer => _inner.Writer;
    public bool IsTerminal => _inner.IsTerminal;
    public int Width => Math.Max(20, (TerminalSize.Pinned?.Cols ?? _inner.Width) - TerminalSize.Margin);
    public int Height => Math.Max(5, TerminalSize.Pinned?.Rows ?? _inner.Height);
    public void SetEncoding(Encoding encoding) => _inner.SetEncoding(encoding);
}
