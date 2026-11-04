namespace Apps2Samsung.Cli;

/// <summary>The parsed command line: the command, its positional arguments and every flag.</summary>
public sealed class CliOptions
{
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; } = new();

    // Output
    public bool Json { get; set; }
    public bool Quiet { get; set; }
    /// <summary>Plain text on a terminal too: no colours, cards or live progress (#975).</summary>
    public bool Plain { get; set; }

    // Where
    public string? Tv { get; set; }
    public string? DataDir { get; set; }
    /// <summary>This machine's address on the TV's network; default: the desktop's adapter choice, else the default route.</summary>
    public string? LocalIp { get; set; }
    /// <summary>Scan VPN and virtual adapters too, as the desktop's rescan does.</summary>
    public bool Virtual { get; set; }

    // Catalog
    public string? Category { get; set; }
    public bool AllVersions { get; set; }
    /// <summary>List the entries the catalog flags adult-only (#850); left out otherwise, as in the apps.</summary>
    public bool Adult { get; set; }
    /// <summary>Most downloaded first, as the apps' "Most downloaded" group, instead of A-Z.</summary>
    public bool Popular { get; set; }
    public string GitHubToken { get; set; } = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
    /// <summary>Sources for this run only (a repository, release or .wgt/.tpk link), on top of the stored ones.</summary>
    public List<string> Sources { get; } = new();
    /// <summary>sources add: the name the source lists under (default: the repository or file name).</summary>
    public string? Name { get; set; }

    // Install
    public string? File { get; set; }
    public string? Version { get; set; }
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Partner { get; set; }
    public bool Launch { get; set; }
    public bool RemoveFirst { get; set; }
    public bool Keep { get; set; }
    public bool Yes { get; set; }
    /// <summary>Allow the remove-and-reinstall that erases the app's data on the TV (#825); separate from <see cref="Yes"/>, as the apps ask it separately.</summary>
    public bool AllowRemove { get; set; }
    /// <summary>Install a package the catalog flags root; separate from <see cref="Yes"/>, as the apps ask it separately.</summary>
    public bool AllowRoot { get; set; }
    public bool NewId { get; set; }
    /// <summary>Back to the package's own id on this TV (#839): forget the rename remembered for it.</summary>
    public bool OwnId { get; set; }

    // Certificate
    public bool Prepare { get; set; }
    public bool ForceLogin { get; set; }
    public bool NoBrowser { get; set; }
    public int LoginTimeoutSeconds { get; set; } = 300;
    public string ManualDuids { get; set; } = string.Empty;
    /// <summary>
    /// <c>--tizen-cert</c> (#988): sign with a Tizen certificate (the SDK's, made here, no Samsung
    /// account and no DUID) instead of the Samsung one. <see cref="TizenLevel"/> is the SDK
    /// distributor to use: the value after the flag (public, partner or platform), else Partner
    /// with --partner, else Public.
    /// </summary>
    public bool TizenCert { get; set; }
    /// <summary><c>--samsung-cert</c>: a Samsung certificate for this run, when the desktop's shared
    /// setting says Tizen (<see cref="Commands.Certificates.UseTizen"/>).</summary>
    public bool SamsungCert { get; set; }
    public string? TizenLevel { get; set; }
    /// <summary><c>--tizen-chain legacy|new</c>: which SDK signer generation (default: legacy where it exists).</summary>
    public string? TizenChain { get; set; }
    /// <summary>cert --export: write the signing certificate to this file (#921).</summary>
    public string? ExportPath { get; set; }
    /// <summary>cert --import: read the signing certificate from this file (#921).</summary>
    public string? ImportPath { get; set; }
    /// <summary><c>sign -o</c>: where the signed copy goes.</summary>
    public string? OutputPath { get; set; }
    /// <summary><c>install --via tvstore</c>: send the package to TV-Store's upload page instead of SDB.</summary>
    public bool ViaTvStore { get; set; }

    public bool Help { get; set; }
    public bool ShowVersion { get; set; }
    /// <summary>Every option as typed, except --tv, --help and --version: the menu (#975) appends them to the command lines it runs, so `menu --data-dir x --adult` carries through.</summary>
    public List<string> Passthrough { get; } = new();

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

            var start = i;
            switch (arg)
            {
                case "-h" or "--help": o.Help = true; break;
                case "-V" or "--version" when o.Command.Length == 0: o.ShowVersion = true; break;
                case "--json": o.Json = true; break;
                case "-q" or "--quiet": o.Quiet = true; break;
                case "--plain": o.Plain = true; break;
                case "-y" or "--yes": o.Yes = true; break;
                case "--tv": o.Tv = Next(arg); if (o.Tv is null) return (o, "--tv needs the TV's IP address"); break;
                case "--local-ip": o.LocalIp = Next(arg); if (o.LocalIp is null) return (o, "--local-ip needs this machine's IP address"); break;
                case "--virtual": o.Virtual = true; break;
                case "--allow-remove": o.AllowRemove = true; break;
                case "--allow-root": o.AllowRoot = true; break;
                case "--data-dir": o.DataDir = Next(arg); if (o.DataDir is null) return (o, "--data-dir needs a path"); break;
                case "--category": o.Category = Next(arg); if (o.Category is null) return (o, "--category needs a category id"); break;
                case "--all-versions": o.AllVersions = true; break;
                case "--adult": o.Adult = true; break;
                case "--popular": o.Popular = true; break;
                case "--github-token": o.GitHubToken = Next(arg) ?? string.Empty; break;
                case "--source":
                {
                    var link = Next(arg);
                    if (string.IsNullOrWhiteSpace(link))
                        return (o, "--source needs a repository, release or .wgt/.tpk link");
                    o.Sources.Add(link);
                    break;
                }
                case "--name": o.Name = Next(arg); if (o.Name is null) return (o, "--name needs the name to list the source under"); break;
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
                case "--samsung-cert": o.SamsungCert = true; break;
                case "--tizen-cert":
                {
                    o.TizenCert = true;
                    // An optional level right after the flag; anything else is the next argument.
                    var next = i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
                    if (next is "public" or "partner" or "platform")
                    {
                        o.TizenLevel = next;
                        i++;
                    }
                    break;
                }
                case "--tizen-chain":
                {
                    var v = Next(arg)?.ToLowerInvariant();
                    if (v is not ("legacy" or "new")) return (o, "--tizen-chain needs legacy or new");
                    o.TizenChain = v;
                    break;
                }
                case "--new-id": o.NewId = true; break;
                case "--own-id": o.OwnId = true; break;
                case "--launch": o.Launch = true; break;
                // Not launching is the default now, as in the apps; still accepted so older scripts keep working.
                case "--no-launch": o.Launch = false; break;
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
                case "--export": o.ExportPath = Next(arg); if (o.ExportPath is null) return (o, "--export needs a file to write"); break;
                case "--import": o.ImportPath = Next(arg); if (o.ImportPath is null) return (o, "--import needs a file to read"); break;
                case "-o" or "--output": o.OutputPath = Next(arg); if (o.OutputPath is null) return (o, "-o needs the file to write"); break;
                case "--via":
                {
                    var v = Next(arg)?.ToLowerInvariant();
                    if (v is "tvstore" or "tv-store") o.ViaTvStore = true;
                    else if (v is not "sdb") return (o, "--via needs sdb or tvstore");
                    break;
                }
                default: return (o, $"unknown option {arg}");
            }
            if (arg is not ("-h" or "--help" or "-V" or "--version" or "--tv"))
                o.Passthrough.AddRange(args[start..(i + 1)]);
        }
        return (o, null);
    }
}
