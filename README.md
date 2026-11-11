# Apps2Samsung

Install any app on your Samsung TV.

<p align="center">
  <img src=".github/jellyfin-tizen-logo.svg" width="220" />
</p>
<p align="center">
  <img src="https://img.shields.io/github/v/release/Apps2Samsung/Apps2Samsung?label=stable&style=for-the-badge" />
  <img src="https://img.shields.io/github/v/release/Apps2Samsung/Apps2Samsung?include_prereleases&label=beta&style=for-the-badge" />
  <img src="https://img.shields.io/badge/Tizen-TVs%20%7C%20projectors%20%7C%20monitors-blue?style=for-the-badge" />
  <img src="https://img.shields.io/badge/OS-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-brightgreen?style=for-the-badge" />
  <a href="https://discord.gg/7mga3zh8Cv"><img src="https://img.shields.io/badge/Discord-Community-7289DA?style=for-the-badge&logo=discord&logoColor=white" /></a>
  <a href="https://score.getplumber.io/github.com/Apps2Samsung/Apps2Samsung"><img src="https://score.getplumber.io/github.com/Apps2Samsung/Apps2Samsung.svg?style=for-the-badge" alt="Plumber Score" /></a>
</p>

Apps2Samsung side-loads apps onto Samsung devices that run Tizen OS: Smart TVs, projectors and
smart monitors. [Jellyfin](https://jellyfin.org), Moonfin, Litefin, TizenTube, the
[community catalog](https://github.com/Apps2Samsung/tizen-community-packages) of 130+ apps, or your
own `.wgt` or `.tpk`. It finds the TV, makes the signing certificate and installs, so you never open
Tizen Studio. It runs on Windows, Linux, macOS, Android and iPhone, in 30 languages, and starts in
yours. Missing a word, or your language? [Help translate on Crowdin](https://crowdin.com/project/jellyfin2samsung).

## Versions

<!-- versions:start -->

| Channel    | Version                                                             | Notes                        |
|------------|---------------------------------------------------------------------|------------------------------|
| **Stable** | [v3.2.1](https://github.com/Apps2Samsung/Apps2Samsung/releases/tag/v3.2.1)                                        | Recommended for most users   |
| **Beta**   | [N/A](#)                                            | Includes new features        |

<!-- versions:end -->

All releases: https://github.com/Apps2Samsung/Apps2Samsung/releases

## Get it

| Where | How |
|---|---|
| **Windows** | `winget install Apps2Samsung.Apps2Samsung`, or the `-win-x64.zip` from a [release](https://github.com/Apps2Samsung/Apps2Samsung/releases). |
| **Linux** | The `.deb`, `.rpm`, AppImage or `.tar.gz` from a release, for x64 and ARM64. NixOS: clone the repository and run `nix-shell`. |
| **macOS** | The `-macos-arm64.dmg` (Apple silicon) or `-macos-x64.dmg` (Intel) from a release. |
| **Android** | [Add to Obtainium](https://apps.obtainium.imranr.dev/redirect?r=obtainium://add/https://github.com/Apps2Samsung/Apps2Samsung) for automatic updates, or add `https://apps2samsung.com/fdroid/repo` as a repository in F-Droid, Droid-ify or Neo Store. Or sideload the `-android.apk` from a release. |
| **iPhone** | Through [SideStore](https://sidestore.io), see below. |
| **Terminal** | `apps2samsung-cli`, one binary per platform on every release, Termux on Android included. See [Command line](#command-line). |

The desktop and Android apps update themselves, with an optional beta channel. Packaging the
desktop app yourself (Nix, Homebrew, a read-only install)? Build it with `-p:DisableSelfUpdate=true`
and it points at your package manager instead of replacing its own files.

### iPhone

The iPhone app has the same screens as the Android one. It is not on the App Store: SideStore
signs it on the phone with your own Apple ID, a free one works.

1. Install SideStore once, following [their guide](https://docs.sidestore.io). It needs a computer and
   a cable that one time, Developer Mode on the iPhone, and the VPN app SideStore asks for.
2. In SideStore, go to **Sources** and add `https://apps2samsung.com/sidestore/apps.json`
   (`https://apps2samsung.com/sidestore/beta/apps.json` for the beta), then install Apps2Samsung from
   it. Or download the `-ios-unsigned.ipa` from a release and add it under **My Apps**.
3. Allow **Local Network** access when iOS asks, or the app cannot find your TV.

A free Apple ID signs apps for 7 days (SideStore refreshes them; if the app stops opening, refresh it
there) and allows 3 sideloaded apps at once. Keep the app in front during an install, iOS pauses it
in the background.

## How it works

1. Put the TV in Developer Mode: [how to](https://github.com/Apps2Samsung/Apps2Samsung/wiki/FAQ#-how-to-enable-developer-mode-on-your-tv).
   The app tells you exactly what is still wrong when a TV is not ready (Developer Mode off, Host PC
   IP pointing elsewhere, a restart needed).
2. Open Apps2Samsung on your computer or phone and pick the TV.
3. Pick an app from the catalog, or your own `.wgt` or `.tpk`, and press Install.

The first install signs you in to Samsung once to make a signing certificate; it is reused for every
install after that. A [video walkthrough](https://www.youtube.com/watch?v=_8mSV5pW-ic) shows the
whole thing.

## What it does

- **Installs anything Tizen**: the community catalog, the Jellyfin builds, or your own `.wgt` or
  `.tpk`. Apps that gain root on the TV carry a `root` badge and ask first; adult apps stay hidden
  until you opt in.
- **Your own sources**: add a GitHub repository, a release, a direct `.wgt`/`.tpk` link or a `.json`
  feed, and its apps sit in the catalog next to the community ones.
- **Asks what an app needs**: an API key, a server address, a port, in a short form before the
  install, remembered for the next update. Jellyfin gets its server, auto-login, themes and plugins.
- **Handles the certificate**: one Samsung sign-in, reused per TV; Public or Partner picked by what
  the app needs; extra TVs pre-authorized by DUID; export and import between devices. Or a Tizen
  certificate from the SDK's own CA, with no Samsung account at all.
- **Manages installed apps**: the catalog's newest version next to the installed one, update in one
  click, launch, uninstall, debug through `chrome://inspect`. Apps that need a newer Tizen than
  the TV has say so before they install.
- **Doubles as a TV remote**: D-pad, playback, volume, channels, power, and typing from your
  keyboard, also into apps that draw their own keyboard. No Developer Mode needed.
- **Diagnoses the TV**: DUID, Tizen version, model, Developer Mode state and a live log console.
- **Backup and restore**: settings and certificates to another PC, Mac or phone in one file.
- **TV-Store on the TV itself**: install [TV-Store](https://github.com/Apps2Samsung/TV-Store) from the
  catalog and arm it once from the toolbox; the TV then browses and installs catalog apps on its
  own, and Apps2Samsung keeps reaching it through TV-Store's relay.

## Command line

Every release ships `apps2samsung-cli` as `Apps2Samsung-cli-<version>-<platform>.zip` or `.tar.gz`.
It runs the same catalog, certificate and install pipeline as the apps, from a terminal, a script or
an AI agent with a shell. Started without arguments on a terminal it opens a menu that shows the
command for every step before it runs it.

```sh
apps2samsung-cli scan                                   # find TVs, and whether each is ready
apps2samsung-cli catalog jellyfin                       # what can be installed (name, description, files)
apps2samsung-cli install "Moonfin" --tv 192.168.1.50    # download, sign, install, launch
apps2samsung-cli installed --tv 192.168.1.50            # what is on the TV, with catalog versions
apps2samsung-cli cert --tv 192.168.1.50                 # which certificate covers this TV
apps2samsung-cli sign app.wgt -o signed.wgt --tv 192.168.1.50   # sign only, for installing another way
apps2samsung-cli sources add github.com/owner/repo      # a source of your own, listed by catalog
```

- `--json` prints one JSON document on stdout, progress stays on stderr. Exit codes: 0 done,
  1 failed, 2 usage, 3 stopped at a pre-install check that `--yes` continues past.
- The first install for a TV opens the Samsung sign-in in a browser on the same machine
  (`--no-browser` prints the address). Certificates are shared with the desktop app's folder.
- `--set key=value` answers what an app asks for before it installs; `catalog` lists the keys.
  `--allow-root` is needed for apps marked `(root)`.
- macOS blocks a downloaded binary until `xattr -d com.apple.quarantine apps2samsung-cli` has run once.
- On Android the `linux-bionic-arm64` build runs in [Termux](https://termux.dev) on Termux's own .NET
  runtime: `pkg install dotnet-runtime-10.0`, unpack the tarball and run
  `./apps2samsung-cli/apps2samsung-cli`. The Samsung sign-in opens in the phone's browser.

`apps2samsung-cli --help` and `<command> --help` have the rest.

## Documentation

- [Quick start](../../wiki), [FAQ](../../wiki/FAQ), [Configuration and Jellyfin settings](../../wiki/Configuration),
  [Screenshots](../../wiki/Screenshots), [Alternative install methods](../../wiki/Alternatives), [Uninstall](../../wiki/Remove)
- Community `.wgt` builds and the catalog's manifests: [tizen-community-packages](https://github.com/Apps2Samsung/tizen-community-packages)
- Orsay-based TVs (before 2015) are not supported.

## Contributing

Bug reports and feature requests go to [Issues](../../issues), ideas and questions to
[Discussions](../../discussions) or [Discord](https://discord.gg/7mga3zh8Cv). Code is welcome, see
[CONTRIBUTING.md](.github/CONTRIBUTING.md); translations happen on
[Crowdin](https://crowdin.com/project/jellyfin2samsung) and sync back here by themselves.

If this tool helped you, you can support its development:

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/M4M71JOT9R)

## Thanks

<a href="https://github.com/Apps2Samsung/Apps2Samsung/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Apps2Samsung/Apps2Samsung" />
</a>

- **jeppevinkel** for the [Jellyfin Tizen builds](https://github.com/jeppevinkel/jellyfin-tizen-builds)
- **RadicalMuffinMan** for [Moonfin](https://github.com/Moonfin-Client/Smart-TV)
- **MoazSalem** for [Litefin](https://github.com/MoazSalem/litefin/)
- **reisxd** for [TizenTube](https://github.com/reisxd/TizenTube)

## License

Apps2Samsung is released under the [GNU General Public License v3.0](LICENSE) (GPL-3.0-only).
Releases up to and including v3.2.1 were MIT licensed. Third-party components redistributed in
the builds (bundled binaries, Samsung CA certificates, NuGet dependencies, fonts and app artwork)
are listed in [NOTICE.md](NOTICE.md).
