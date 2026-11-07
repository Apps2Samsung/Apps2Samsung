using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Apps2Samsung.Diagnostics;

namespace Apps2Samsung.Cli;

/// <summary>
/// The session log (#975): one <c>debug_&lt;time&gt;.log</c> per run under <see cref="Folder"/>, the
/// same file the desktop and phone apps write (Core's <see cref="FileLog"/>). It holds every step,
/// warning and error the CLI printed, every SDB command with its exit status, and the TV's own install
/// output, so a run that went wrong on a phone can be read afterwards. <c>apps2samsung-cli logs</c>
/// and the menu's "Session log" say where it is.
/// <para>
/// It also takes <see cref="Console.Out"/> over for what other layers print (the TV's install log
/// comes from the SDB engine that way). That text belongs in the log, not between the progress lines:
/// on a terminal it broke the live step line, which then stacked one copy per percentage, and with
/// <c>--json</c> it landed in the JSON. It goes to stderr when the output is plain text, where the
/// progress lines are, and only to the log otherwise. The CLI's own output keeps the real writers
/// (<see cref="Output.StdOut"/>).
/// </para>
/// </summary>
public static class CliLog
{
    /// <summary>How many logs stay in the folder, this run's included; older ones are deleted at start.</summary>
    public const int Keep = 20;

    public static string Folder => Path.Combine(CliPaths.CliDir, "logs");

    /// <summary>This run's log file, null before <see cref="Start"/> or when it could not be made.</summary>
    public static string? Current => FileLog.CurrentLogFile;

    private static StrayOutput? s_stray;

    /// <summary>Starts the log once per process (the menu runs every command in the same one); each call notes the command.</summary>
    public static void Start(Output output, CliOptions options)
    {
        if (s_stray is null)
        {
            Output.KeepStandardWriters();
            Prune();
            FileLog.Initialize(Folder);
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            Trace.WriteLine($"[cli] apps2samsung-cli {version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier}), .NET {Environment.Version}{(IsTermux ? ", Termux" : "")}");
            s_stray = new StrayOutput();
            try { Console.SetOut(s_stray); } catch { /* a host without a console keeps its own */ }
        }
        s_stray.Echo = output.Rich ? null : Output.StdErr;
        // The verb and its words; flags are left out, a --github-token among them.
        Trace.WriteLine($"[cli] > {options.Command} {string.Join(' ', options.Arguments)}{(options.Tv is { } tv ? $" --tv {tv}" : "")}".TrimEnd());
    }

    /// <summary>Running in Termux on Android: where a phone keeps its files differs, which the hints say.</summary>
    public static bool IsTermux => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERMUX_VERSION"))
        || (Environment.GetEnvironmentVariable("PREFIX")?.Contains("com.termux", StringComparison.Ordinal) ?? false);

    /// <summary>The logs in <see cref="Folder"/>, newest first.</summary>
    public static IReadOnlyList<FileInfo> List()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists)
                return Array.Empty<FileInfo>();
            return dir.GetFiles("debug_*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
        }
    }

    /// <summary>How to get a log off this device for a bug report, one line.</summary>
    public static string ShareHint(string path) => IsTermux
        ? $"To attach it to a GitHub issue: termux-setup-storage once, then cp \"{path}\" ~/storage/downloads/ and pick it from Downloads."
        : "Attach that file to the GitHub issue or discussion.";

    // Room for this run's file: the names sort by time, so the oldest go first.
    private static void Prune()
    {
        try
        {
            foreach (var old in List().Skip(Keep - 1))
            {
                try { old.Delete(); } catch { /* in use or gone: next time */ }
            }
        }
        catch { /* a log must never stop a command */ }
    }

    /// <summary>What other layers print on stdout: whole lines to the log, and as they are to <see cref="Echo"/> when set.</summary>
    private sealed class StrayOutput : TextWriter
    {
        private readonly StringBuilder _line = new();

        public TextWriter? Echo { get; set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            Echo?.Write(value);
            if (value == '\n')
                Flush();
            else if (value != '\r')
                _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            foreach (var ch in value)
                Write(ch);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void Flush()
        {
            if (_line.Length == 0)
                return;
            Trace.WriteLine($"[console:out] {_line}");
            _line.Clear();
        }
    }
}
