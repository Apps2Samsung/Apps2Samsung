namespace Apps2Samsung.Cli;

/// <summary>The parsed command line: the command, its positional arguments and every flag.</summary>
public sealed class CliOptions
{
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; } = new();

    // Output
    public bool Json { get; set; }
    public bool Quiet { get; set; }

    // Where
    public string? Tv { get; set; }
    public string? DataDir { get; set; }

    // Catalog
    public string? Category { get; set; }
    public bool AllVersions { get; set; }
    public string GitHubToken { get; set; } = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;

    // Install
    public string? File { get; set; }
    public string? Version { get; set; }
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Partner { get; set; }
    public bool NoLaunch { get; set; }
    public bool RemoveFirst { get; set; }
    public bool Keep { get; set; }
    public bool Yes { get; set; }

    // Certificate
    public bool Prepare { get; set; }
    public bool ForceLogin { get; set; }
    public bool NoBrowser { get; set; }
    public int LoginTimeoutSeconds { get; set; } = 300;
    public string ManualDuids { get; set; } = string.Empty;

    public bool Help { get; set; }
    public bool ShowVersion { get; set; }

    /// <summary>Parses <paramref name="args"/>; a message on a flag it does not know, null when fine.</summary>
    public static (CliOptions Options, string? Error) Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                if (o.Command.Length == 0) o.Command = arg.ToLowerInvariant();
                else o.Arguments.Add(arg);
                continue;
            }

            string? Next(string name)
            {
                if (i + 1 >= args.Length)
                    return null;
                return args[++i];
            }

            switch (arg)
            {
                case "-h" or "--help": o.Help = true; break;
                case "-V" or "--version" when o.Command.Length == 0: o.ShowVersion = true; break;
                case "--json": o.Json = true; break;
                case "-q" or "--quiet": o.Quiet = true; break;
                case "-y" or "--yes": o.Yes = true; break;
                case "--tv": o.Tv = Next(arg); if (o.Tv is null) return (o, "--tv needs the TV's IP address"); break;
                case "--data-dir": o.DataDir = Next(arg); if (o.DataDir is null) return (o, "--data-dir needs a path"); break;
                case "--category": o.Category = Next(arg); if (o.Category is null) return (o, "--category needs a category id"); break;
                case "--all-versions": o.AllVersions = true; break;
                case "--github-token": o.GitHubToken = Next(arg) ?? string.Empty; break;
                case "--file": o.File = Next(arg); if (o.File is null) return (o, "--file needs a file or variant name"); break;
                case "--version": o.Version = Next(arg); if (o.Version is null) return (o, "--version needs a release tag"); break;
                case "--set":
                {
                    var kv = Next(arg);
                    var eq = kv?.IndexOf('=') ?? -1;
                    if (kv is null || eq <= 0)
                        return (o, "--set needs key=value");
                    o.Settings[kv[..eq]] = kv[(eq + 1)..];
                    break;
                }
                case "--partner": o.Partner = true; break;
                case "--no-launch": o.NoLaunch = true; break;
                case "--remove-first": o.RemoveFirst = true; break;
                case "--keep": o.Keep = true; break;
                case "--prepare": o.Prepare = true; break;
                case "--force-login": o.ForceLogin = true; break;
                case "--no-browser": o.NoBrowser = true; break;
                case "--login-timeout":
                {
                    var v = Next(arg);
                    if (!int.TryParse(v, out var seconds) || seconds <= 0)
                        return (o, "--login-timeout needs a number of seconds");
                    o.LoginTimeoutSeconds = seconds;
                    break;
                }
                case "--duids": o.ManualDuids = Next(arg) ?? string.Empty; break;
                default: return (o, $"unknown option {arg}");
            }
        }
        return (o, null);
    }
}
