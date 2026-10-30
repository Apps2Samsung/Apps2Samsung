namespace Apps2Samsung.Cli;

/// <summary>The help text. Written for a person at a terminal and for an agent reading it once.</summary>
public static class Help
{
    public const string Usage = """
        usage: apps2samsung-cli <command> [arguments] [options]

        commands
          scan [--virtual]             find Samsung TVs on this network and whether each can be installed to
          tv <ip>                      check one TV: Developer Mode, debug port, Tizen version, DUID
          catalog [words...]           list installable apps; words narrow the list by name or description
          installed --tv <ip>          apps on the TV, with the catalog version next to the installed one
          install <app|file> --tv <ip> install a catalog app by name, or a local .wgt/.tpk file
          uninstall <appId> --tv <ip>  remove an app; the app id as `installed` prints it
          launch <appId> --tv <ip>
          cert [--tv <ip>]             the signing certificates on this machine; --prepare makes one now,
                                       --export / --import move one between devices
          tvstore --tv <ip>            arm the TV-Store app on the TV: hand it the signing certificate once

        options
          --json                       one JSON document on stdout instead of text (progress stays on stderr)
          -q, --quiet                  no progress lines
          -y, --yes                    continue past pre-install warnings
          --tv <ip>                    the TV's IP address (from `scan`)
          --local-ip <ip>              this machine's address on the TV's network (default: the desktop app's
                                       network setting, else the default route; set it on a VPN)
          --data-dir <path>            where certificates and caches live (default: the desktop app's folder)
          --github-token <token>       GitHub token for the catalog's API calls (or GITHUB_TOKEN)
          -h, --help                   this text, or `<command> --help` for one command
          --version                    the CLI version

        exit codes: 0 done, 1 failed, 2 usage, 3 stopped at a check (rerun with --yes to continue)
        """;

    private const string Catalog = """
        usage: apps2samsung-cli catalog [words...] [--category <id>] [--all-versions] [--adult] [--popular] [--json]

        Lists the apps Apps2Samsung can install: name, category, description, the files or variants to
        choose from (with the version each would install) and where the source lives. Words narrow the
        list to apps whose name or description contains all of them. Categories: media, iptv, streaming,
        games, casting, tools, other. --all-versions keeps every release of an app with a version
        history (Jellyfin) instead of only the newest. Apps the catalog marks adult-only are left out
        unless --adult is given; they are listed with (18+) then. Apps that gain root on the TV are
        listed with (root); `install` needs --allow-root for them. Each app shows its all-time downloads
        when apps2samsung.com has the counts; --popular lists the most downloaded first.

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
          --partner             sign with a Partner certificate (default: the desktop app's certificate
                                setting; apps that need Partner get it by themselves)
          --new-id              install under a fresh package id, for a TV that refuses this app's own id
                                ([118, -22]); remembered per TV, so later installs update the same tile
          --own-id              back to the app's own package id on this TV: forget the id remembered by
                                --new-id (the TV then shows a new tile next to the renamed copy)
          --launch              open the app on the TV afterwards
          --remove-first        uninstall the installed copy before installing (erases its data on the TV)
          --keep                keep the downloaded package in the cache folder
          -y, --yes             continue past pre-install warnings
          --allow-remove        allow removing the installed copy when only a remove-and-reinstall gets
                                the new one on (erases the app's data on the TV)
          --allow-root          install an app the catalog marks (root): it gains root on the TV, and
                                afterwards Host PC IP has to be 127.0.0.1, which locks this machine out
          --no-browser          print the Samsung sign-in address instead of opening a browser
          --login-timeout <s>   how long to wait for the Samsung sign-in (default 300)
          --force-login         make a new certificate even when a usable one exists
          --duids <list>        extra TV DUIDs to put in the certificate (comma-separated; default: the
                                desktop app's setting)

        The first install for a TV creates a signing certificate, which needs a Samsung account sign-in
        in a browser on this machine. A certificate made by the Apps2Samsung desktop app on the same
        machine is reused; so is one made here, for every later install on that TV.

        Pre-install checks (Developer Mode off, the TV's Developer IP pointing elsewhere, another subnet)
        stop the install with exit code 3 unless --yes is given; they are warnings, a TV can misreport.
        A local file is copied first: your own .wgt/.tpk is never renamed, patched or re-signed.
        """;

    private const string Cert = """
        usage: apps2samsung-cli cert [--tv <ip>] [--partner] [--prepare] [--duids <DUID,...>] [--force-login] [--json]
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
        --export <file> writes the signing certificate on its own (no settings) to a file, every
        usable level or the Partner one with --partner, for the desktop app, the phone or another
        machine to import; installs there then need no second Samsung sign-in.
        --import <file> shows what the file holds (level, account, validity, TVs covered; with --tv
        whether that TV is among them) and what it would replace here, then imports it. Replacing a
        certificate already on this machine needs --yes. An expired certificate is refused.
        """;

    private const string TvStore = """
        usage: apps2samsung-cli tvstore --tv <ip> [--partner] [--force-login] [--json]

        Arms the Apps2Samsung TV-Store app on the TV: hands it the signing certificate this machine
        installs with, so the TV signs and installs catalog apps on its own from then on, with no PC
        or phone in the loop. TV-Store has to be on the TV already (install its .wgt like any
        package); the certificate is made here first, with the Samsung sign-in, when none covers the
        TV yet, at the level the desktop app's certificate setting picks (--partner: Partner). As in the
        desktop app, the TV gets whichever certificate on this machine covers it, Public first.

        On the TV, Developer Mode's Host PC IP must be set to the TV's own address, or its sdbd
        refuses the connection TV-Store makes to it.
        """;

    public static string For(string command) => command switch
    {
        "catalog" or "search" or "apps" => Catalog,
        "install" => Install,
        "cert" or "certificate" => Cert,
        "tvstore" or "tv-store" => TvStore,
        _ => Usage,
    };
}
