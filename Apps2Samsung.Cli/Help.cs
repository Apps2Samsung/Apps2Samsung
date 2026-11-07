namespace Apps2Samsung.Cli;

/// <summary>The help text. Written for a person at a terminal and for an agent reading it once.</summary>
public static class Help
{
    public const string Usage = """
        usage: apps2samsung-cli <command> [arguments] [options]
               apps2samsung-cli              on a terminal: the menu (TV, app, install)

        commands
          menu [--tv <ip>]             the menu: every step is one of the commands below, shown before it runs
          scan [--virtual]             find Samsung TVs on this network and whether each can be installed to
          tv <ip>                      check one TV: Developer Mode, debug port, Tizen version, DUID
          catalog [words...]           list installable apps; words narrow the list by name or description
          installed --tv <ip>          apps on the TV, with the catalog version next to the installed one
                                       (an app with several files is compared with the one this TV got)
          install <app|file> --tv <ip>  install a catalog app by name, or a local .wgt/.tpk file
          sign <file> -o <out> --tv <ip>  sign a local .wgt/.tpk for a TV without installing it
          uninstall <appId> --tv <ip>  remove an app; the app id as `installed` prints it
          launch <appId> --tv <ip>
          cert [--tv <ip>]             the signing certificates on this machine; --prepare makes one now,
                                       --export / --import move one between devices
          tvstore --tv <ip>            arm the TV-Store app on the TV: hand it the signing certificate once
          tvstore log --tv <ip>        save TV-Store's own log from the TV (TV-Store open on the TV)
          leftovers --tv <ip>          the packages left in the TV's install staging folder; --clear deletes
                                       them, --delete <file> one file (the toolbox's Install leftovers)
          logs [show]                  where the session logs are; `logs show` prints the newest, for a bug report
          sources [add|remove ...]     your own sources: a GitHub repository, a release, a .wgt/.tpk link or a
                                       .json feed, listed by `catalog` next to the community apps

        options
          --json                       one JSON document on stdout instead of text (progress stays on stderr)
          -q, --quiet                  no progress lines
          --plain                      text only: no colours, cards or live progress (as when piped, or with NO_COLOR)
          -y, --yes                    continue past pre-install warnings
          --tv <ip>                    the TV's IP address (from `scan`)
          --local-ip <ip>              this machine's address on the TV's network (default: the desktop app's
                                       network setting, else the default route; set it on a VPN)
          --data-dir <path>            where certificates and caches live (default: the desktop app's folder)
          --github-token <token>       GitHub token for the catalog's API calls (or GITHUB_TOKEN)
          --source <link>              a source for this run only (repeatable): a GitHub repository, a release,
                                       a .wgt/.tpk link or a .json feed, listed as if added with `sources add`
          -h, --help                   this text, or `<command> --help` for one command
          --version                    the CLI version

        exit codes: 0 done, 1 failed, 2 usage, 3 stopped at a check (rerun with --yes to continue)
        """;

    private const string Catalog = """
        usage: apps2samsung-cli catalog [words...] [--category <id>] [--all-versions] [--adult] [--popular] [--source <link>] [--json]

        Lists the apps Apps2Samsung can install: name, category, description, the files or variants to
        choose from (with the version each would install) and where the source lives. Words narrow the
        list to apps whose name or description contains all of them. Categories: media, iptv, streaming,
        games, casting, tools, other. --all-versions keeps every release of an app with a version
        history (Jellyfin) instead of only the newest. Apps the catalog marks adult-only are left out
        unless --adult is given; they are listed with (18+) then. Apps that gain root on the TV are
        listed with (root); `install` needs --allow-root for them. Each app shows its all-time downloads
        when apps2samsung.com has the counts; --popular lists the most downloaded first.

        An app that needs values from you before it installs (an API key, a server address) lists them
        under "settings"; pass them to `install` with --set key=value. Apps from your own sources
        (`sources`, or --source <link> for this run) are listed with (own source).
        """;

    private const string Install = """
        usage: apps2samsung-cli install <app|file> --tv <ip> [options]

          <app>                 a catalog name as `catalog` prints it (case does not matter; a unique part of it works)
          <file>                a local .wgt or .tpk instead of a catalog app
          --file <name>         which file or variant of the app, when it has several (default: the app's default file)
          --version <tag>       which release, for an app with a version history (implies --all-versions)
          --from <source>       which source, when several offer the app: official, or a repository's name
                                or id (`catalog` labels each app with its source)
          --set key=value       a value the app asks for before it installs (repeatable; `catalog` lists the keys)
          --partner             sign with a Partner certificate (default: the desktop app's certificate
                                setting; apps that need Partner get it by themselves)
          --tizen-cert [level]  sign with a Tizen certificate instead of a Samsung one: made here from
                                the Tizen SDK's own CA, no Samsung account, no DUID (level: public,
                                partner or platform; default public, partner with --partner). A TV
                                that does not trust it says so; a Tizen 5.0 set takes public and
                                refuses partner and platform (issue #988). Without the flag the
                                desktop app's Settings > Installing choice (Samsung or Tizen) decides
          --samsung-cert        sign with a Samsung certificate for this run, when that setting says Tizen
          --tizen-chain <gen>   which SDK distributor signer generation the Tizen certificate uses:
                                legacy (2012, default where it exists, the one a TV took) or new
                                (2022; platform only has this one)
          --new-id              install under a fresh package id, for a TV that refuses this app's own id
                                ([118, -22]); remembered per TV, so later installs update the same tile
          --own-id              back to the app's own package id on this TV: forget the id remembered by
                                --new-id (the TV then shows a new tile next to the renamed copy)
          --launch              open the app on the TV afterwards
          --remove-first        uninstall the installed copy before installing (erases its data on the TV)
          --keep                keep the downloaded package in the cache folder
          --via tvstore         send the package to TV-Store's upload page instead of installing over SDB:
                                TV-Store comes to the front and asks on the TV, and signs it itself
                                (done by itself when SDB does not answer and TV-Store is running)
          -y, --yes             continue past pre-install warnings
          --allow-remove        allow removing the installed copy when only a remove-and-reinstall gets
                                the new one on (erases the app's data on the TV)
          --allow-root          install an app the catalog marks (root): it gains root on the TV, and
                                afterwards Host PC IP has to be 127.0.0.1; this machine then reaches
                                the TV only through TV-Store's relay (TV-Store 0.3.14+, turned on in
                                its Settings), which the CLI dials by itself on port 26201
          --no-browser          print the Samsung sign-in address instead of opening a browser
          --login-timeout <s>   how long to wait for the Samsung sign-in (default 300)
          --force-login         make a new certificate even when a usable one exists
          --duids <list>        extra TV DUIDs to put in the certificate (comma-separated; default: the
                                desktop app's setting)

        Which file of an app with several (variants, or builds per Tizen version) a TV got is remembered
        on this machine, shared with the desktop app: `installed` then compares with that file, and the
        menu's Update installs it again. --file picks another and becomes the remembered one.

        The first install for a TV creates a signing certificate, which needs a Samsung account sign-in
        in a browser on this machine. A certificate made by the Apps2Samsung desktop app on the same
        machine is reused; so is one made here, for every later install on that TV.

        Pre-install checks (Developer Mode off, the TV's Developer IP pointing elsewhere, another subnet)
        stop the install with exit code 3 unless --yes is given; they are warnings, a TV can misreport.
        A local file is copied first: your own .wgt/.tpk is never renamed, patched or re-signed.

        A TV that SDB cannot reach (Host PC IP 127.0.0.1 without TV-Store's relay, or the checks above)
        but that runs TV-Store gets the package through TV-Store's upload page instead: customized as
        for an install (--set values included), not signed here; nothing is installed until Install is
        pressed on the TV, so the result is the file waiting there (exit 0, "via": "tvstore").
        """;

    private const string Sign = """
        usage: apps2samsung-cli sign <file> -o <signed file> (--tv <ip> | --duids <DUID>) [--partner] [--json]
               apps2samsung-cli sign <file> -o <signed file> --tizen-cert [public|partner|platform] [--tizen-chain legacy|new] [--json]
               apps2samsung-cli sign <file> -o <signed file> --tv <ip> --samsung-cert [--json]

        Signs a local .wgt or .tpk with the certificate that covers the TV and writes the signed copy to
        -o, without installing it: for tools that install another way (a root service on the TV,
        TV-Store's upload page). The certificate is chosen and made as for `install` (the desktop app's
        certificate setting, Partner with --partner or when the package needs it; a Samsung sign-in in
        the browser when none covers the TV yet). --duids names the TV by its DUID instead of reading it
        over SDB, for a TV whose Host PC IP points elsewhere (127.0.0.1); the first DUID is the TV.
        --tizen-cert signs with a Tizen certificate instead (the Tizen SDK's own CA, made here, no
        Samsung account): it names no TV, so neither --tv nor --duids is needed. A Tizen 5.0 TV takes
        the public one (legacy signer generation) and refuses the others; see `install --help` and
        issue #988. Without the flag the desktop app's Samsung-or-Tizen setting decides;
        --samsung-cert overrides it for this run.
        The input file is never changed.
        """;

    private const string Cert = """
        usage: apps2samsung-cli cert [--tv <ip>] [--partner] [--prepare] [--duids <DUID,...>] [--force-login] [--json]
               apps2samsung-cli cert --prepare --tizen-cert [public|partner|platform] [--tizen-chain legacy|new] [--force-login]
               apps2samsung-cli cert --export <file> [--partner]
               apps2samsung-cli cert --import <file> [--tv <ip>] [--yes]

        Shows the Public and Partner signing certificates on this machine: whether one exists, until
        when it is valid and which TVs (DUIDs) it covers. With --tv, also whether that TV is covered.
        --prepare creates or extends the certificate for --tv now (the Samsung sign-in happens here
        instead of during the first install), at the level the desktop app's certificate setting picks;
        --partner selects the Partner level instead.
        --prepare --duids <DUID,...> without --tv does the same for TVs whose DUID you already know,
        without reading them over SDB: for a TV whose Host PC IP points elsewhere (127.0.0.1 for a
        TV that roots itself, or another computer). The first DUID is the TV the profile is made for;
        every DUID listed is covered.
        --prepare --tizen-cert makes a Tizen certificate instead (the Tizen SDK's own CA, no Samsung
        account, no TV named in it): "Tizen - Public/Partner/Platform" next to the Samsung profiles,
        reused while valid, remade with --force-login; --tizen-chain new makes the "... New" profile
        with the 2022 SDK signer instead. The listing shows the Tizen profiles too. Without the flag
        the desktop app's Samsung-or-Tizen setting decides; --samsung-cert overrides it for this run.
        --export <file> writes the signing certificate on its own (no settings) to a file, every
        usable level or the Partner one with --partner, for the desktop app, the phone or another
        machine to import; installs there then need no second Samsung sign-in.
        --import <file> shows what the file holds (level, account, validity, TVs covered; with --tv
        whether that TV is among them) and what it would replace here, then imports it. Replacing a
        certificate already on this machine needs --yes. An expired certificate is refused.
        """;

    private const string TvStore = """
        usage: apps2samsung-cli tvstore --tv <ip> [--partner] [--force-login] [--json]
               apps2samsung-cli tvstore log --tv <ip> [-o <file>] [--json]

        Arms the Apps2Samsung TV-Store app on the TV: hands it the signing certificate this machine
        installs with, so the TV signs and installs catalog apps on its own from then on, with no PC
        or phone in the loop. TV-Store has to be on the TV already (install its .wgt like any
        package); the certificate is made here first, with the Samsung sign-in, when none covers the
        TV yet, at the level the desktop app's certificate setting picks (--partner: Partner). As in the
        desktop app, the TV gets whichever certificate on this machine covers it, Public first.

        On the TV, Developer Mode's Host PC IP must be the TV's own address or 127.0.0.1 (TV-Store
        0.3.14 or newer), or its sdbd refuses the connection TV-Store makes to it. With 127.0.0.1 this
        machine reaches the TV through TV-Store's relay (port 26201), once it is turned on there.

        tvstore log saves TV-Store's own log (this run and the previous one) for a bug report, from
        http://<ip>:8077/log.txt, as tvstore_<ip>_<time>.log in the current folder or to -o
        <file>. TV-Store has to be open on the TV; Developer Mode is not needed.
        """;

    private const string Leftovers = """
        usage: apps2samsung-cli leftovers --tv <ip> [--json]
               apps2samsung-cli leftovers --tv <ip> --clear [--json]
               apps2samsung-cli leftovers --tv <ip> --delete <file> [--json]

        Every package that gets installed is first copied to a staging folder on the TV and left
        there afterwards; the TV's own clean-up does nothing on retail firmware. This lists that
        folder: name, size, date, and whether a file is a package. --clear deletes every package and
        keeps any other file; --delete takes one file by name, package or not. Installed apps are
        never touched. The same card as the desktop and phone apps' TV toolbox, "Install leftovers".

        The folder is read by the Apps2Samsung Debug agent, a small app on the TV (needs Developer
        Mode and a developer connection from this machine, as an install does). The agent is
        installed like any package when the TV lacks it, or replaced when it is older than the one
        this build carries: the first time, that takes the signing certificate, so a Samsung sign-in
        in the browser when none covers the TV yet. It is stopped again when the command ends.
        """;

    private const string Sources = """
        usage: apps2samsung-cli sources [--json]
               apps2samsung-cli sources add <link> [--name <name>] [--category <id>] [--partner]
               apps2samsung-cli sources add <https://…/repository.json.signed> [--fingerprint <key>] [--yes]
               apps2samsung-cli sources remove <link|name>

        Your own sources, the same list the desktop app keeps under Settings > Installing > Your
        sources (shared through its settings.json). `catalog` lists their apps next to the community
        ones, marked (own source), and reads them again on every run, so a developer's new release
        shows up without waiting for tizen-community-packages.

        A link is one of:
          github.com/owner/repo  (or owner/repo)      the newest release with a .wgt or .tpk
          a release page (…/releases/tag/<tag>)       that one release
          https://host/path/App.wgt  (or .tpk)        the file itself, dated by its Last-Modified header
          https://host/path/apps.json                 a feed: every provider in it, so one link publishes a
                                                      set of apps; the file has the shape of the installer's
                                                      own third-party-apps.json ({ "providers": [ { "id",
                                                      "url" (a GitHub releases API URL), "displayName",
                                                      "category", "certLevel", "buildInfo": { "description" } } ] }),
                                                      or is a bare array of such providers

          https://host/path/repository.json.signed   a signed repository: a developer's own list of apps,
                                                      signed with their key (the format is docs/repositories.md
                                                      in tizen-community-packages)

        Signed repositories: `add` shows the repository's name, domain, maintainer and its key's
        fingerprint and asks whether to trust that key (or takes --fingerprint <key>, that key and no
        other, or --yes). From then on every catalog load checks the repository against that stored
        key. A valid signature means the apps come from that publisher, not that Apps2Samsung checked
        them. When the publisher's key changes, its apps are left out until you run `sources add` with
        the same link again and approve the new key; when the repository cannot be verified, its apps
        are listed from the last verified copy but cannot be installed. A file from a repository is
        installed only when its bytes match the digest the repository signed. Apps are labelled with
        their source; an installed app is updated only from the source it came from. The other kinds
        of link are labelled Unverified.

        --name is what the catalog lists it under (default: the repository or file name); --category is
        one of media, iptv, streaming, games, casting, tools, other; --partner marks an app that needs the
        Partner certificate. `remove` takes the link as typed, the link in another spelling, or the name.
        For a single run without storing anything, pass --source <link> to `catalog` or `install`.
        """;

    private const string Logs = """
        usage: apps2samsung-cli logs [show] [--json]

        Every command writes a session log: each step, warning and error it printed, every SDB command
        with its result and the TV's own install output. The last 20 runs are kept, in the cli/logs
        folder of the data directory (~/.config/Apps2Samsung/cli/logs on Linux and in Termux).

          logs                         the folder and the newest logs
          logs show                    print the newest log, the run before this one: `logs show > a2s.log`
                                       makes a file to attach to a GitHub issue

        In the menu, "Session log" shows the log the menu is writing right now.
        """;

    public static string For(string command) => command switch
    {
        "catalog" or "search" or "apps" => Catalog,
        "sources" or "source" => Sources,
        "install" => Install,
        "sign" => Sign,
        "cert" or "certificate" => Cert,
        "tvstore" or "tv-store" => TvStore,
        "leftovers" or "staging" => Leftovers,
        "logs" or "log" => Logs,
        _ => Usage,
    };
}
