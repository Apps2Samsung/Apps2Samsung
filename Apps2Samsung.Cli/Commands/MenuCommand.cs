using System.Reflection;
using Apps2Samsung.Agent;
using Apps2Samsung.Catalog;
using Apps2Samsung.Design;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Apps2Samsung.Cli.Commands;

/// <summary>
/// The menu (#975): what <c>apps2samsung-cli</c> opens when it is started without arguments on a
/// terminal, and the <c>menu</c> verb. It scans, lists the TVs, and from there it is arrow keys:
/// pick a TV, pick an app, answer what the app asks, confirm, watch the install. Every action is one
/// of the verbs: the menu builds the command line, prints it ("The same as: …") so it can go into
/// a script, and runs it through <see cref="Dispatch.RunLineAsync"/>. Nothing happens here that
/// the verbs cannot do, and nothing runs without being shown first.
/// <para>
/// Every screen starts clean (<see cref="Screen"/>): the terminal is cleared and the header
/// redrawn, so a phone shows one thing at a time instead of the whole session. What a command
/// printed stays until a key is pressed (<see cref="PauseAsync"/>), and so does a warning, so a
/// redraw never takes away something that was not read yet.
/// </para>
/// </summary>
public static class MenuCommand
{
    public static Task<int> RunAsync(CliServices s, CancellationToken ct)
    {
        if (!s.Out.Rich)
            return Task.FromResult(s.Out.Fail("the menu needs a terminal (and no --json or --plain): run it from a terminal window, or `apps2samsung-cli --help` lists the commands", Output.Usage));
        return new Menu(s, ct).RunAsync();
    }
}

internal sealed class Menu
{
    private sealed record Item(string Label, object? Tag = null);
    private sealed record Tv(TvState State, NetworkDevice Device);

    private readonly CliServices _s;
    private readonly CancellationToken _ct;
    private readonly IAnsiConsole _con;
    private readonly List<Tv> _tvs = new();
    private readonly string? _localIp;
    private readonly bool _unicode;

    public Menu(CliServices s, CancellationToken ct)
    {
        _s = s;
        _ct = ct;
        _con = s.Out.Terminal!;
        _unicode = s.Out.Unicode;
        _localIp = LocalIp.Of(s);
    }

    private string A(string text) => $"[{Views.AccentHex}]{text}[/]";
    private bool Narrow => _s.Out.Width < Views.NarrowWidth;
    /// <summary>A menu entry: the action in the accent colour with what it does beside it; the action alone on a phone, where the line has no room.</summary>
    private Item Act(string action, string description, object tag) => new(Narrow ? A(action) : $"{A(action.PadRight(22))}{M(description)}", tag);
    private string M(string text) => $"[{Views.MutedHex}]{text}[/]";
    /// <summary>A choice cut to the terminal's width (#975): the prompt's "> " and the column's padding take the rest.</summary>
    private string Fit(string label) => Views.Fit(label, _s.Out.Width - 4, _unicode);
    private string Dot => _unicode ? "·" : "-";
    private static string E(string? text) => Views.Esc(text);

    public async Task<int> RunAsync()
    {
        // Whether the TV list is already on screen: after a scan it is, after a TV's menu it is not.
        var listed = false;

        // --tv on the command line: straight into that TV's menu, no scan.
        if (!string.IsNullOrWhiteSpace(_s.Options.Tv))
        {
            Screen();
            var given = await AddAddressAsync(_s.Options.Tv);
            if (given is not null)
                await TvMenuAsync(given);
            else
                await PauseAsync();
        }
        else
        {
            await ScanAsync();
            listed = true;
        }

        while (true)
        {
            var tv = await PickTvAsync(listed);
            listed = false;
            if (tv is null)
                return Output.Ok;
            await TvMenuAsync(tv);
        }
    }

    /// <summary>A clean screen: the terminal cleared, the header redrawn. Every screen of the menu starts here.</summary>
    private void Screen()
    {
        _s.Out.Clear();
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        version = version.Split('+')[0];
        Show(new Markup($"[bold]Apps2Samsung[/] {M($"{E(version)} {Dot} this {(OperatingSystem.IsAndroid() ? "phone" : "machine")} is {E(_localIp ?? "not on a network")}")}"));
        Show(new Markup(M($"{(_unicode ? "↑↓" : "up/down")} move {Dot} enter choose {Dot} ctrl+c quit")));
        _con.WriteLine();
    }

    // ---- the TVs ----

    private async Task ScanAsync()
    {
        Screen();
        _s.Out.Progress("Scanning the network for Samsung TVs…");
        var found = await TizenDeveloperInfo.ScanAsync(_s.Network, _ct, _s.Options.Virtual);
        if (found.Count == 0 && !_s.Options.Virtual)
        {
            _s.Out.Progress("Nothing found; scanning again with VPN and virtual adapters included…");
            found = await TizenDeveloperInfo.ScanAsync(_s.Network, _ct, virtualScan: true);
        }
        _tvs.Clear();
        var localIps = LocalIp.All(_s);
        foreach (var d in found.OrderBy(d => d.IpAddress, StringComparer.Ordinal))
            _tvs.Add(new Tv(TvState.From(d, _localIp, localIps), d));
        ShowTvs();
    }

    private async Task<Tv?> AddAddressAsync(string ip)
    {
        _s.Out.Progress($"Checking {ip}…");
        var device = await _s.Network.ValidateManualTizenAddress(ip, _ct);
        if (device is null)
        {
            _s.Out.Warn($"No Samsung TV answers at {ip} (nothing on port 8001 or 26101). Is the TV on and on this network?");
            _s.Out.Idle(Glyph.Failed);
            return null;
        }
        device = await TizenDeveloperInfo.ReadAsync(device, _ct);
        var tv = new Tv(TvState.From(device, _localIp, LocalIp.All(_s)), device);
        _tvs.RemoveAll(t => t.State.Ip == tv.State.Ip);
        _tvs.Add(tv);
        _tvs.Sort((a, b) => string.CompareOrdinal(a.State.Ip, b.State.Ip));
        _s.Out.Idle();
        return tv;
    }

    private void ShowTvs()
    {
        var none = new[]
        {
            "No Samsung TV found on this network.",
            "A TV must be on, on the same network, and answer on port 8001. \"Another address\" below checks one directly.",
        };
        Show(Views.Tvs(_tvs.Select(t => t.State).ToList(), _localIp, none, _unicode));
    }

    /// <summary>The TV list and its choices; <paramref name="listed"/> when the cards are on screen already (a scan just drew them).</summary>
    private async Task<Tv?> PickTvAsync(bool listed = false)
    {
        if (!listed)
        {
            Screen();
            ShowTvs();
        }
        var items = new List<Item>();
        foreach (var tv in _tvs)
            items.Add(new Item(TvLabel(tv), tv));
        items.Add(new Item($"{A("+")} Another address{Ellipsis}", "address"));
        items.Add(new Item($"{A("↻".Length == 1 && _unicode ? "↻" : "*")} Scan again", "scan"));
        items.Add(new Item(M("Session log"), "log"));
        items.Add(new Item(M("Quit"), "quit"));

        while (true)
        {
            var pick = await SelectAsync(M("TVs on this network"), items);
            switch (pick.Tag)
            {
                case Tv tv:
                    return tv;
                case "address":
                {
                    var ip = await AskAsync("The TV's IP address:", allowEmpty: true);
                    if (string.IsNullOrWhiteSpace(ip))
                        break;
                    var added = await AddAddressAsync(ip.Trim());
                    if (added is not null)
                        return added;
                    break;
                }
                case "scan":
                    await ScanAsync();
                    return await PickTvAsync(listed: true);
                case "log":
                    await SessionLogAsync();
                    return await PickTvAsync();
                default:
                    return null;
            }
        }
    }

    private string TvLabel(Tv tv)
    {
        var st = tv.State;
        var led = st.Ready ? Views.OkHex : Views.WarnHex;
        var who = (st.Name ?? st.Model ?? "").Replace("[TV] ", "", StringComparison.Ordinal);
        var status = st.Ready ? $"[{Views.OkHex}]ready[/]" : $"[{Views.WarnHex}]not ready[/]";
        // On a phone the LED carries the status; the word would wrap the line.
        return Narrow
            ? $"[{led}]{(_unicode ? "●" : "*")}[/] [bold]{E(st.Ip)}[/]  {E(who)}"
            : $"[{led}]{(_unicode ? "●" : "*")}[/] [bold]{E(st.Ip)}[/]  {E(who)}  {status}";
    }

    // ---- one TV ----

    private async Task TvMenuAsync(Tv tv)
    {
        while (true)
        {
            Screen();
            Show(Views.TvCard(tv.State, _unicode));
            var ip = tv.State.Ip;
            var cert = Certificates.Describe(Certificates.Level(_s));
            var certNote = cert.Exists && cert.Usable ? $"{cert.Level} {Dot} valid to {cert.ValidUntil} {Dot} covers {cert.Duids.Count} TV{(cert.Duids.Count == 1 ? "" : "s")}" : "none yet; the first install signs in to Samsung";
            var items = new List<Item>
            {
                Act("Install an app", "from the catalog, or a .wgt/.tpk file", "install"),
                Act("Installed apps", "with updates from the catalog", "installed"),
                Act("Certificate", E(certNote), "cert"),
                Act("TV-Store", "arm TV-Store on the TV with the certificate", "tvstore"),
                Act("TV-Store log", "save TV-Store's own log for a bug report", "tvstorelog"),
                Act("Install leftovers", "packages left in the TV's staging folder", "leftovers"),
                Act("Developer Mode", tv.State.Ready ? "ready; the guide for this TV" : "the guide for this TV", "devmode"),
                Act("Session log", "what this session did, for a bug report", "log"),
                new(M($"{(_unicode ? "←" : "<-")} Back to the TVs"), "back"),
            };
            var pick = await SelectAsync(M("This TV"), items);
            switch (pick.Tag)
            {
                case "install": await InstallAsync(tv); break;
                case "installed": await InstalledAsync(tv); break;
                case "cert": await CertificateAsync(tv); break;
                case "tvstore":
                    if (await ConfirmAsync($"Hand the signing certificate to TV-Store on {E(ip)}? TV-Store must be installed and open on the TV."))
                        await RunAsync("tvstore", "--tv", ip);
                    break;
                case "tvstorelog":
                    await RunAsync("tvstore", "log", "--tv", ip);
                    break;
                case "devmode":
                    tv = await DeveloperModeAsync(tv);
                    break;
                case "leftovers": await LeftoversAsync(tv); break;
                case "log": await SessionLogAsync(); break;
                default: return;
            }
        }
    }

    // ---- the session log ----

    /// <summary>Where this session's log is (#975), its last lines, and how to get it off the device.</summary>
    private async Task SessionLogAsync()
    {
        Screen();
        var path = CliLog.Current;
        if (path is null)
        {
            _s.Out.Warn($"this session has no log: {CliLog.Folder} could not be written");
            await PauseAsync();
            return;
        }
        _con.MarkupLine($"[bold]This session's log[/]");
        _con.MarkupLine(A(E(path)));
        _con.WriteLine();
        foreach (var line in Tail(path, Math.Clamp(_s.Out.Height - 12, 5, 20)))
            _con.MarkupLine(M(E(line)));
        _con.WriteLine();
        _con.MarkupLine(E(CliLog.ShareHint(path)));
        _con.MarkupLine(M($"Earlier sessions: apps2samsung-cli logs {Dot} the last {CliLog.Keep} are kept"));
        _con.WriteLine();
        await PauseAsync();
    }

    /// <summary>The last lines of a file the log listener still has open.</summary>
    private static IReadOnlyList<string> Tail(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > count)
                    lines.Dequeue();
            }
            return lines.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new[] { $"(could not read it: {ex.Message})" };
        }
    }

    // ---- leftovers ----

    /// <summary>
    /// The toolbox's Install leftovers card (#681): the TV's staging folder as the debug agent lists
    /// it, then Clear or one file. Each choice is its own `leftovers` run, which attaches the agent
    /// again; the listing here is read directly so the file names can be offered.
    /// </summary>
    private async Task LeftoversAsync(Tv tv)
    {
        var ip = tv.State.Ip;
        Screen();
        Show(Views.TvCard(tv.State, _unicode));
        List<LeftoversCommand.Row> rows;
        string summary;
        try
        {
            await using var agent = await LeftoversCommand.AttachAsync(_s, ip, _ct);
            if (!agent.SupportsStaging)
            {
                _s.Out.Warn($"the debug agent on the TV is v{agent.AgentVersion}; reading the staging folder needs v{DebugAgentClient.StagingSince} or later");
                _s.Out.Idle(Glyph.Failed);
                await PauseAsync();
                return;
            }
            var listing = await LeftoversCommand.ListAsync(_s, agent, _ct);
            rows = listing.Files.Select(LeftoversCommand.ToRow).ToList();
            summary = LeftoversCommand.Summary(_s, listing);
            foreach (var e in listing.Errors)
                _s.Out.Warn(e);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _s.Out.Warn(ex switch
            {
                DebugAgentInstallException install => _s.Strings.GetString(install.Key),
                LeftoversCommand.TvUnreachableException => $"{E(ip)} did not list its apps ({E(ex.Message)}): Developer Mode on, Host PC IP this {(OperatingSystem.IsAndroid() ? "phone" : "machine")}'s address, then restart the TV",
                _ => $"the staging folder could not be read: {E(ex.Message)}",
            });
            _s.Out.Idle(Glyph.Failed);
            await PauseAsync();
            return;
        }
        _s.Out.Idle();
        Show(Views.Leftovers(rows, summary, ip, _s.Out.Width, _unicode));
        ShowLine(new[] { "leftovers", "--tv", ip });
        if (rows.Count == 0)
        {
            await PauseAsync();
            return;
        }

        var packages = rows.Count(r => r.IsPackage);
        var items = new List<Item>();
        if (packages > 0)
            items.Add(Act("Clear", $"delete the {packages} package{(packages == 1 ? "" : "s")}, keep the rest", "clear"));
        items.Add(Act($"Delete one file{Ellipsis}", "package or not", "delete"));
        items.Add(new Item(M($"{(_unicode ? "←" : "<-")} Back"), "back"));
        var pick = await SelectAsync(M($"Leftovers on {E(ip)}"), items);
        switch (pick.Tag)
        {
            case "clear":
            {
                var args = new[] { "leftovers", "--tv", ip, "--clear" };
                ShowLine(args);
                if (await ConfirmAsync($"Delete the {packages} package{(packages == 1 ? "" : "s")} from the staging folder on {E(ip)}? Installed apps are not touched."))
                    await RunAsync(args);
                return;
            }
            case "delete":
            {
                var choices = rows.Select(r => new Item($"{E(r.Name)}  {M(E(InstalledApp.FormatSize(r.Size)))}", r)).ToList();
                choices.Add(new Item(M($"{(_unicode ? "←" : "<-")} Back"), "back"));
                if ((await SelectAsync(M("Which file"), choices)).Tag is not LeftoversCommand.Row file)
                    return;
                var args = new[] { "leftovers", "--tv", ip, "--delete", file.Name };
                ShowLine(args);
                if (await ConfirmAsync($"Delete {E(file.Name)} from the TV?", defaultValue: false))
                    await RunAsync(args);
                return;
            }
            default:
                return;
        }
    }

    // ---- install ----

    private async Task InstallAsync(Tv tv)
    {
        var ip = tv.State.Ip;
        Screen();
        _s.Out.Progress("Loading the app catalog…");
        CatalogLoader.Catalog catalog;
        try { catalog = await _s.Catalog.LoadAsync(false, _ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _s.Out.Warn($"the catalog could not be loaded: {ex.Message}");
            _s.Out.Idle(Glyph.Failed);
            await PauseAsync();
            return;
        }
        if (catalog.FailedProviders > 0)
            _s.Out.Warn($"{catalog.FailedProviders} source(s) could not be read; their apps are missing from this list");
        _s.Out.Idle();

        var apps = CommunityAppList.Visible(catalog.Entries, _s.Options.Adult)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The picker filters as you type; the labels are plain text so the search sees what is on screen.
        var file = new Item("A .wgt or .tpk file on this device…", "file");
        var choices = new List<Item> { file };
        choices.AddRange(apps.Select(r =>
        {
            var e = CatalogCommand.ToEntry(r);
            var parts = new List<string> { e.Name };
            var version = e.Files.FirstOrDefault(f => f.Default)?.Version ?? e.Files.FirstOrDefault()?.Version ?? e.Tag;
            if (version is not null) parts.Add(version.StartsWith('v') ? version : $"v{version}");
            parts.Add(e.Category);
            if (e.Downloads is { } n) parts.Add($"{n:N0} {(_unicode ? "↓" : "downloads")}");
            if (e.OwnSource) parts.Add("own source");
            if (e.RequiresPartner) parts.Add("partner");
            if (e.Root) parts.Add("root");
            return new Item(E(string.Join("  ", parts)), r);
        }));
        choices.Add(new Item(M($"{(_unicode ? "←" : "<-")} Back"), "back"));

        var prompt = new SelectionPrompt<Item>()
            .Title($"{M("Install on")} [bold]{E(ip)}[/] {E(tv.State.Name)}")
            .UseConverter(i => Fit(i.Label))
            .PageSize(Math.Clamp(_s.Out.Height - 8, 5, 16))
            .MoreChoicesText(M("(more below)"))
            .HighlightStyle(new Style(decoration: Decoration.Bold))
            .EnableSearch()
            .SearchPlaceholderText(M("type to search"))
            .AddChoices(choices);
        var pick = await prompt.ShowAsync(_con, _ct);

        var args = new List<string> { "install" };
        string what;
        if (pick.Tag is "back")
            return;
        if (pick.Tag is "file")
        {
            var path = await AskAsync("The path of the .wgt or .tpk file:", allowEmpty: true);
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = path.Trim().Trim('"', '\'');
            if (!File.Exists(path) || !(path.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
            {
                _s.Out.Warn($"{path} is not a .wgt or .tpk file that exists.");
                await PauseAsync();
                return;
            }
            args.Add(Path.GetFullPath(path));
            what = Path.GetFileName(path);
        }
        else
        {
            var release = (GitHubRelease)pick.Tag!;
            var entry = CatalogCommand.ToEntry(release);
            Show(Views.Catalog(new[] { entry }, _s.Out.Width, _unicode));
            args.Add(release.Name);
            what = release.Name;

            // Which file, when there is a choice; then the values the file asks for.
            var chosen = entry.Files.FirstOrDefault(f => f.Default) ?? entry.Files.FirstOrDefault();
            if (entry.Files.Count > 1)
            {
                // The file names go when a row would not fit the terminal: the variant says which file,
                // and the Tizen version at the end is what the choice turns on.
                List<Item> FileRows(bool withFile) => entry.Files.Select(f =>
                {
                    var label = f.Variant ?? Path.GetFileNameWithoutExtension(f.File);
                    var file = withFile && f.Variant is not null ? $"  {M(E(f.File))}" : "";
                    var rest = $"{(f.Version is not null ? $"  v{E(f.Version)}" : "")}{(f.Tizen is not null ? $"  {M($"Tizen {E(f.Tizen)}+")}" : "")}{(f.Default ? $"  {M("(default)")}" : "")}";
                    return new Item($"{E(label)}{file}{rest}", f);
                }).ToList();
                var files = FileRows(withFile: true);
                if (files.Any(i => Views.Cells(Markup.Remove(i.Label)) > _s.Out.Width - 4))
                    files = FileRows(withFile: false);
                var picked = await SelectAsync(M("Which file"), files);
                chosen = (CatalogCommand.FileInfo)picked.Tag!;
                if (!chosen.Default)
                    args.AddRange(new[] { "--file", chosen.Variant ?? Path.GetFileNameWithoutExtension(chosen.File) });
            }
            if (chosen?.Settings is { Count: > 0 } settings)
            {
                _con.MarkupLine(M($"{E(release.Name)} asks for a few values before it installs."));
                foreach (var st in settings)
                {
                    var value = await AskAsync($"{E(st.Label)}{(st.Required ? "" : M(" (optional)"))}:", allowEmpty: !st.Required, defaultValue: st.Default, hint: st.HelpUrl);
                    if (!string.IsNullOrWhiteSpace(value))
                        args.AddRange(new[] { "--set", $"{st.Key}={value.Trim()}" });
                }
            }
            if (release.IsRoot)
            {
                _con.MarkupLine($"[{Views.WarnHex}]{E(release.Name)} gains root on the TV and runs a background service from boot.[/] Afterwards Developer Mode's Host PC IP has to be 127.0.0.1, and this device then reaches the TV only through TV-Store's relay.");
                if (!await ConfirmAsync("Install it anyway?", defaultValue: false))
                    return;
                args.Add("--allow-root");
            }
        }

        args.AddRange(new[] { "--tv", ip });
        if (await ConfirmAsync("Open the app on the TV afterwards?"))
            args.Add("--launch");
        if (!tv.State.Ready)
        {
            _con.MarkupLine($"[{Views.WarnHex}]This TV is not ready: {E(tv.State.NotReadyReason)}[/]");
            if (!await ConfirmAsync("Try the install anyway?", defaultValue: false))
                return;
            args.Add("--yes");
        }
        ShowLine(args);
        if (await ConfirmAsync($"Install {E(what)} on {E(ip)}?"))
            await RunAsync(args.ToArray());
    }

    // ---- installed ----

    private async Task InstalledAsync(Tv tv)
    {
        var ip = tv.State.Ip;
        Screen();
        List<InstalledCommand.Row> rows;
        try { rows = await InstalledCommand.ReadAsync(_s, ip, _ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _s.Out.Warn($"the TV did not list its apps: {ex.Message}");
            _s.Out.Idle(Glyph.Failed);
            await PauseAsync();
            return;
        }
        _s.Out.Idle();
        Show(Views.InstalledApps(rows, ip, _s.Out.Width, _unicode));
        ShowLine(new[] { "installed", "--tv", ip });
        if (rows.Count == 0)
        {
            await PauseAsync();
            return;
        }

        while (true)
        {
            var items = new List<Item>();
            foreach (var r in rows.Where(r => r.State == "update-available"))
                items.Add(new Item($"{A("Update")} {E(r.Title)}{(r.CatalogVariant is null ? "" : $" {M(E(r.CatalogVariant))}")} {M($"v{E(r.Version ?? "?")} {(_unicode ? "→" : "->")} v{E(r.CatalogVersion ?? "?")}")}", ("update", r)));
            items.Add(new Item($"{A("Open an app on the TV")}{Ellipsis}", "launch"));
            items.Add(new Item($"{A("Remove an app")}{Ellipsis}", "remove"));
            items.Add(new Item(M($"{(_unicode ? "←" : "<-")} Back"), "back"));
            var pick = await SelectAsync(M($"Installed on {E(ip)}"), items);
            switch (pick.Tag)
            {
                case ("update", InstalledCommand.Row r):
                {
                    // The same file this TV got last time, when the app has several (#975).
                    var args = InstalledCommand.InstallArgs(r, ip).ToArray();
                    ShowLine(args);
                    if (await ConfirmAsync($"Update {E(r.Title)}{(r.CatalogVariant is null ? "" : $" ({E(r.CatalogVariant)})")} to v{E(r.CatalogVersion ?? "?")}?"))
                        await RunAsync(args);
                    return;
                }
                case "launch":
                {
                    var app = await PickAppAsync(rows, "Which app");
                    if (app is null) break;
                    // One line of result; the list stays and the choices come back under it.
                    await RunInlineAsync("launch", app.AppId, "--tv", ip);
                    break;
                }
                case "remove":
                {
                    var app = await PickAppAsync(rows.Where(r => r.Removable).ToList(), "Which app to remove");
                    if (app is null) break;
                    var args = new[] { "uninstall", app.AppId, "--tv", ip };
                    ShowLine(args);
                    if (await ConfirmAsync($"Remove {E(app.Title)} from the TV? Its data on the TV goes with it.", defaultValue: false))
                        await RunAsync(args);
                    return;
                }
                default:
                    return;
            }
        }
    }

    private async Task<InstalledCommand.Row?> PickAppAsync(IReadOnlyList<InstalledCommand.Row> rows, string title)
    {
        if (rows.Count == 0)
        {
            _con.MarkupLine(M("Nothing to choose from."));
            return null;
        }
        var items = rows.Select(r => new Item($"{E(r.Title)}{(r.Version is null ? "" : $" {M($"v{E(r.Version)}")}")}  {M(E(r.AppId))}", r)).ToList();
        items.Add(new Item(M($"{(_unicode ? "←" : "<-")} Back"), "back"));
        var pick = await SelectAsync(M(title), items);
        return pick.Tag as InstalledCommand.Row;
    }

    // ---- certificate ----

    private async Task CertificateAsync(Tv tv)
    {
        var ip = tv.State.Ip;
        Screen();
        await RunInlineAsync("cert", "--tv", ip);
        var items = new List<Item>
        {
            Act("Prepare it now", "the Samsung sign-in, before the first install", "prepare"),
            Act($"Export it to a file{Ellipsis}", "for the desktop app or the phone", "export"),
            new(M($"{(_unicode ? "←" : "<-")} Back"), "back"),
        };
        var pick = await SelectAsync(M("Certificate"), items);
        switch (pick.Tag)
        {
            case "prepare":
                await RunAsync("cert", "--tv", ip, "--prepare");
                break;
            case "export":
            {
                var path = await AskAsync("Write the certificate to:", allowEmpty: true, defaultValue: Path.Combine(Environment.CurrentDirectory, "apps2samsung-certificate.zip"));
                if (!string.IsNullOrWhiteSpace(path))
                    await RunAsync("cert", "--export", path.Trim());
                break;
            }
        }
    }

    // ---- Developer Mode ----

    private async Task<Tv> DeveloperModeAsync(Tv tv)
    {
        while (true)
        {
            Screen();
            var problem = Enum.TryParse<DevModeProblem>(tv.State.Problem, out var p) ? p : DevModeProblem.None;
            Show(Guide(tv, problem));
            var items = new List<Item>
            {
                Act("Check the TV again", "after a step on the TV", "again"),
                new(M($"{(_unicode ? "←" : "<-")} Back"), "back"),
            };
            var pick = await SelectAsync(M("Developer Mode"), items);
            if (pick.Tag is not "again")
                return tv;
            var fresh = await AddAddressAsync(tv.State.Ip);
            if (fresh is null)
                await PauseAsync();
            else
                tv = fresh;
        }
    }

    private IRenderable Guide(Tv tv, DevModeProblem problem)
    {
        string L(string key) => _s.Strings.GetString(key);
        var rows = new List<IRenderable>
        {
            new Markup($"[bold]{E(L(DevModeGuide.TitleKey(problem)))}[/]"),
            new Markup($"[{(problem == DevModeProblem.None ? Views.OkHex : Views.WarnHex)}]{E(DevModeGuide.Status(problem, tv.Device, _localIp, L))}[/]"),
            new Text(""),
        };
        foreach (var step in DevModeGuide.Steps(problem))
        {
            var number = step.Needed ? A(step.Number.ToString()) : M(step.Number.ToString());
            var title = E(L(step.TitleKey)) + (step.ShowsLocalIp && _localIp is not null ? $": [bold]{E(_localIp)}[/]" : "");
            rows.Add(new Markup(step.Needed ? $"  {number}  {title}" : $"  {number}  {M(title)}"));
            rows.Add(new Padder(new Markup(M(E(L(step.HintKey)))), new Padding(5, 0, 0, 0)));
        }
        if (problem == DevModeProblem.None)
            rows.Add(new Markup(M("  Nothing to do on the TV.")));
        return Views.Card(new Rows(rows), $"[bold]{E(tv.State.Ip)}[/] {E(tv.State.Name)}", Views.DimHex, _unicode);
    }

    // ---- the pieces ----

    private string Ellipsis => _unicode ? "…" : "...";

    private void Show(IRenderable renderable) => _s.Out.Show(renderable);

    /// <summary>The command line that does what was just chosen, so it can go into a script.</summary>
    private void ShowLine(IReadOnlyList<string> args)
    {
        _s.Out.Idle();
        _con.MarkupLine($"{M("The same as:")} {A("apps2samsung-cli " + E(Shell.Join(args.Concat(_s.Options.Passthrough).ToList())))}");
    }

    /// <summary>Runs a command and leaves what it printed on screen until a key is pressed; the next screen clears it.</summary>
    private async Task RunAsync(params string[] args)
    {
        await RunInlineAsync(args);
        await PauseAsync();
    }

    /// <summary>Runs a command whose output is the top of the screen that follows (the certificate, an app opened), so the choices come right under it.</summary>
    private async Task RunInlineAsync(params string[] args)
    {
        _s.Out.Idle();
        var line = args.Concat(_s.Options.Passthrough).ToList();
        if (args[0] is not ("install" or "uninstall" or "leftovers"))
            ShowLine(args);
        _con.WriteLine();
        await Dispatch.RunLineAsync(line, _ct);
        _con.WriteLine();
    }

    /// <summary>Waits for a key, so a result or a warning is read before the next screen clears it. Ctrl+C quits here as everywhere.</summary>
    private async Task PauseAsync()
    {
        _s.Out.Idle();
        _con.Markup(M("any key to go back "));
        await _con.Input.ReadKeyAsync(true, _ct);
        _ct.ThrowIfCancellationRequested();
        _con.WriteLine();
    }

    private async Task<Item> SelectAsync(string title, IReadOnlyList<Item> items)
    {
        _s.Out.Idle();
        var prompt = new SelectionPrompt<Item>()
            .Title(title)
            .UseConverter(i => Fit(i.Label))
            .PageSize(Math.Clamp(_s.Out.Height - 6, 4, 14))
            .MoreChoicesText(M("(more below)"))
            .HighlightStyle(new Style(decoration: Decoration.Bold))
            .WrapAround()
            .AddChoices(items);
        return await prompt.ShowAsync(_con, _ct);
    }

    private async Task<bool> ConfirmAsync(string markup, bool defaultValue = true)
    {
        _s.Out.Idle();
        var prompt = new ConfirmationPrompt(markup) { DefaultValue = defaultValue };
        return await prompt.ShowAsync(_con, _ct);
    }

    private async Task<string> AskAsync(string markup, bool allowEmpty, string? defaultValue = null, string? hint = null)
    {
        _s.Out.Idle();
        if (hint is not null)
            _con.MarkupLine(M($"see {E(hint)}"));
        var prompt = new TextPrompt<string>(markup).PromptStyle(new Style(decoration: Decoration.Bold));
        if (allowEmpty)
            prompt.AllowEmpty();
        if (defaultValue is not null)
            prompt.DefaultValue(defaultValue);
        return await prompt.ShowAsync(_con, _ct);
    }
}

/// <summary>Quoting for the command line the menu shows: enough for a copy into bash, PowerShell or Termux.</summary>
internal static class Shell
{
    public static string Join(IReadOnlyList<string> args) => string.Join(" ", args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:=@+,%".Contains(c)))
            return arg;
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
