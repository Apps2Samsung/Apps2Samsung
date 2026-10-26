namespace Apps2Samsung.Cli;

/// <summary>The help text. Written for a person at a terminal and for an agent reading it once.</summary>
public static class Help
{
    public const string Usage = """
        usage: apps2samsung-cli <command> [arguments] [options]

        commands
          scan                         find Samsung TVs on this network and whether each can be installed to
          tv <ip>                      check one TV: Developer Mode, debug port, Tizen version, DUID
          catalog [words...]           list installable apps; words narrow the list by name or description
          installed --tv <ip>          apps on the TV, with the catalog version next to the installed one
          install <app|file> --tv <ip> install a catalog app by name, or a local .wgt/.tpk file
          uninstall <packageId> --tv <ip>
          launch <appId> --tv <ip>
          cert [--tv <ip>]             the signing certificates on this machine; --prepare makes one now

        options
          --json                       one JSON document on stdout instead of text (progress stays on stderr)
          -q, --quiet                  no progress lines
          -y, --yes                    continue past pre-install warnings and allow removing an installed copy
          --tv <ip>                    the TV's IP address (from `scan`)
          --data-dir <path>            where certificates and caches live (default: the desktop app's folder)
          --github-token <token>       GitHub token for the catalog's API calls (or GITHUB_TOKEN)
          -h, --help                   this text, or `<command> --help` for one command
          --version                    the CLI version

        exit codes: 0 done, 1 failed, 2 usage, 3 stopped at a check (rerun with --yes to continue)
        """;

    private const string Catalog = """
        usage: apps2samsung-cli catalog [words...] [--category <id>] [--all-versions] [--adult] [--json]

        Lists the apps Apps2Samsung can install: name, category, description, the files or variants to
        choose from (with the version each would install) and where the source lives. Words narrow the
        list to apps whose name or description contains all of them. Categories: media, iptv, streaming,
        games, casting, tools, other. --all-versions keeps every release of an app with a version
        history (Jellyfin) instead of only the newest. Apps the catalog marks adult-only are left out
        unless --adult is given; they are listed with (18+) then.

        An app that needs values from you before it installs (an API key, a server address) lists them
        under "settings"; pass them to `install` with --set key=value.
        """;

    private const string Install = """
        usage: apps2samsung-cli install <app|file> --tv <ip> [options]

          <app>                 a catalog name as `catalog` prints it (case does not matter; a unique part of it works)
          <file>                a local .wgt or .tpk instead of a catalog app
          --file <name>         which file or variant of the app, when it has several (default: the app's default file)
          --version <tag>       which release, for an app with a version history (implies --all-versions)
          --set key=value       a value the app asks for before it installs (repeatable; `catalog` lists the keys)
          --partner             sign with a Partner certificate (done by itself for apps that need one)
          --new-id              install under a fresh package id, for a TV that refuses this app's own id
                                ([118, -22]); remembered per TV, so later installs update the same tile
          --no-launch           do not open the app on the TV afterwards
          --remove-first        uninstall the installed copy before installing (erases its data on the TV)
          --keep                keep the downloaded package in the cache folder
          -y, --yes             continue past pre-install warnings; allow the remove-and-retry recovery
          --no-browser          print the Samsung sign-in address instead of opening a browser
          --login-timeout <s>   how long to wait for the Samsung sign-in (default 300)
          --force-login         make a new certificate even when a usable one exists
          --duids <list>        extra TV DUIDs to put in the certificate (comma-separated)

        The first install for a TV creates a signing certificate, which needs a Samsung account sign-in
        in a browser on this machine. A certificate made by the Apps2Samsung desktop app on the same
        machine is reused; so is one made here, for every later install on that TV.

        Pre-install checks (Developer Mode off, the TV's Developer IP pointing elsewhere, another subnet)
        stop the install with exit code 3 unless --yes is given; they are warnings, a TV can misreport.
        """;

    private const string Cert = """
        usage: apps2samsung-cli cert [--tv <ip>] [--partner] [--prepare] [--duids <DUID,...>] [--force-login] [--json]

        Shows the Public and Partner signing certificates on this machine: whether one exists, until
        when it is valid and which TVs (DUIDs) it covers. With --tv, also whether that TV is covered.
        --prepare creates or extends the certificate for --tv now (the Samsung sign-in happens here
        instead of during the first install); --partner selects the Partner level for that.
        --prepare --duids <DUID,...> without --tv does the same for TVs whose DUID you already know,
        without reading them over SDB: for a TV whose Host PC IP points elsewhere (127.0.0.1 for a
        TV that roots itself, or another computer). The first DUID is the TV the profile is made for;
        every DUID listed is covered.
        """;

    public static string For(string command) => command switch
    {
        "catalog" or "search" or "apps" => Catalog,
        "install" => Install,
        "cert" or "certificate" => Cert,
        _ => Usage,
    };
}
