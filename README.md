# Apps2Samsung

*Install any app on your Samsung TV.*

<p align="center">
  <img src=".github/jellyfin-tizen-logo.svg" width="220" />
</p>
<p align="center">
  <img src="https://img.shields.io/github/v/release/Apps2Samsung/Apps2Samsung?label=stable&style=for-the-badge" />
  <img src="https://img.shields.io/github/v/release/Apps2Samsung/Apps2Samsung?include_prereleases&label=beta&style=for-the-badge" />
  <img src="https://img.shields.io/badge/Tizen-TVs%20%7C%20projectors%20%7C%20monitors-blue?style=for-the-badge" />
  <img src="https://img.shields.io/badge/OS-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS%20(beta)-brightgreen?style=for-the-badge" />
  <a href="https://discord.gg/7mga3zh8Cv">
    <img src="https://img.shields.io/badge/Discord-Community-7289DA?style=for-the-badge&logo=discord&logoColor=white" />
  </a>
</p>

<p align="center">
  <b>Apps2Samsung</b> is a small cross-platform tool that side-loads <b>any app</b> onto <b>Samsung devices running Tizen OS</b> (Smart TVs, projectors and smart monitors):
  <a href="https://jellyfin.org">Jellyfin</a>, Moonlight, Moonfin, Litefin, TizenTube and the whole <a href="https://github.com/Apps2Samsung/tizen-community-packages">community catalog</a>, or your own <code>.wgt</code>.
  <br/>
  It handles device detection, certificates, and installation so you don’t have to fight with Tizen Studio or manual sideloading.
  <br/><br/>
  🌐 Available in <b>30 languages</b> and starts in yours. Missing a word, or your language? <a href="https://crowdin.com/project/jellyfin2samsung">Help translate on Crowdin</a>.
</p>

---

## 📦 Current Versions

<!-- versions:start -->

| Channel    | Version                                                             | Notes                        |
|------------|---------------------------------------------------------------------|------------------------------|
| **Stable** | [v3.1.0](https://github.com/Apps2Samsung/Apps2Samsung/releases/tag/v3.1.0)                                        | Recommended for most users   |
| **Beta**   | [v3.2.0-beta](https://github.com/Apps2Samsung/Apps2Samsung/releases/tag/v3.2.0-beta)                                            | Includes new features        |

<!-- versions:end -->

👉 All releases: https://github.com/Apps2Samsung/Apps2Samsung/releases

### 📱 Get the Android app (auto-updating)

The phone app is the mobile installer head: run the whole install from your phone.

- **Obtainium**: tracks this repo's GitHub releases and updates automatically when a new **stable** is published. Add it with one tap:
  [Add to Obtainium](https://apps.obtainium.imranr.dev/redirect?r=obtainium://add/https://github.com/Apps2Samsung/Apps2Samsung), or paste `https://github.com/Apps2Samsung/Apps2Samsung` into Obtainium's *Add App*.
- **F-Droid**: add our repository in the F-Droid app (Settings → Repositories → Add): `https://apps2samsung.com/fdroid/repo`. It serves the latest **stable** and updates automatically. Works in F-Droid, Droid-ify and Neo Store.
- **Direct**: grab the `-android.apk` from any [release](https://github.com/Apps2Samsung/Apps2Samsung/releases) and sideload it.

### 🍏 Try the iPhone app (beta, testers wanted)

Since v3 there is an iPhone app too, with the same screens as the Android one. It isn't on the App Store: you install it with [SideStore](https://sidestore.io), which signs it on the phone with your own Apple ID (a free one works). **It is new and hasn't been through a full install on a real iPhone yet**, so expect rough edges and please report what you find in [#753](https://github.com/Apps2Samsung/Apps2Samsung/issues/753).

1. Install SideStore once, following [their guide](https://docs.sidestore.io) (needs a computer and a cable once, plus Developer Mode on the iPhone and the VPN app SideStore asks for).
2. In SideStore, go to **Sources → +** and add `https://apps2samsung.com/sidestore/beta/apps.json`, then install **Apps2Samsung** from it. Or download the `-ios-unsigned.ipa` from the latest [beta release](https://github.com/Apps2Samsung/Apps2Samsung/releases) and add it under **My Apps → +**.
3. Open the app and allow **Local Network** access when iOS asks: without it the app can't find your TV.

Good to know:
- A free Apple ID signs apps for **7 days**. SideStore refreshes them in the background; if the app stops opening, open SideStore and refresh it. If SideStore itself expired, you need the computer again.
- A free Apple ID allows **3 sideloaded apps** at once, SideStore included.
- Keep the app in front during an install: iOS pauses apps in the background.
- Waking the TV from the remote may not work on iPhone (iOS blocks the network broadcast it needs for apps signed with a free Apple ID).

### 🐧 Arch Linux (AUR)

The official [`apps2samsung`](https://aur.archlinux.org/packages/apps2samsung) package is published by this repo's release workflow, so it follows every **stable** release:

```sh
yay -S apps2samsung      # or paru -S apps2samsung
```

It installs the same build as the `.deb`/`.rpm` (x86_64 and aarch64). Other Linux distros: take the `.AppImage`, `.deb`, `.rpm` or `.tar.gz` from the [releases](https://github.com/Apps2Samsung/Apps2Samsung/releases).

**Packaging it yourself?** Build the desktop app with `-p:DisableSelfUpdate=true` to compile out the in-place self-update action (the app will still check for and announce new versions, but points to your package manager instead of replacing its own files). Useful for read-only or package-manager-owned installs such as Nix or Homebrew.

### 🤖 Command line (scripts and AI agents)

Every release also ships `apps2samsung-cli`: one self-contained binary per platform (`Apps2Samsung-cli-<version>-<platform>.zip` or `.tar.gz` on the [releases page](https://github.com/Apps2Samsung/Apps2Samsung/releases)). It runs the same catalog, certificate and install pipeline as the apps, from a terminal, a script, or an AI agent that has a shell.

```sh
apps2samsung-cli scan                                   # find TVs, and whether each is ready
apps2samsung-cli catalog jellyfin                       # what can be installed (name, description, files)
apps2samsung-cli install "Moonfin" --tv 192.168.1.50    # download, sign, install, launch
apps2samsung-cli installed --tv 192.168.1.50            # what is on the TV, with catalog versions
apps2samsung-cli cert --tv 192.168.1.50                 # which certificate covers this TV
```

- `--json` on any command prints one JSON document on stdout (progress stays on stderr); exit codes are 0 done, 1 failed, 2 usage, 3 stopped at a pre-install check that `--yes` continues past.
- The first install for a TV creates a signing certificate, which needs a Samsung account sign-in in a browser on the same machine (`--no-browser` prints the address instead). Certificates are shared with the desktop app's folder, so a TV set up there needs no sign-in here.
- Apps that ask for values before installing (an API key, a server address) take them as `--set key=value`; `catalog` lists the keys.
- Apps marked `(root)` in `catalog` gain root on the TV and only install with `--allow-root`. `cert --prepare --duids <DUID>` makes a certificate for a TV you can't reach over SDB, such as one whose Host PC IP is `127.0.0.1`.
- macOS blocks a downloaded binary until `xattr -d com.apple.quarantine apps2samsung-cli` has run once.

---

## ✨ What It Does

- **Finds your TV**: scans the network for Samsung TVs, or enter an IP by hand, and says exactly what's wrong when a TV isn't ready (Developer Mode off, IP mismatch, reversed IP, needs a power cycle).
- **Handles the certificate**: signs in to Samsung once and reuses the certificate per TV. Pre-authorize extra TVs by DUID; Public/Partner certificates are picked automatically when an app needs restricted privileges.
- **Installs anything Tizen**: the community catalog (110+ apps), the Jellyfin builds, or your own `.wgt` / native `.tpk`.
- **A catalog you can browse**: apps grouped by category with search, a sheet per app (description, versions or variants, source link), and favorites at the top. Adult (18+) apps stay hidden until you opt in; apps that gain root on the TV carry a `root` badge and ask for confirmation before they install.
- **Asks what an app needs**: apps that need your own values (an API key, a server address, a port) get a short form before installing, filled in for you again on the next update.
- **Manages installed apps**: list them with their icons and the catalog's newest version, update in one click, or uninstall, launch, stop, debug (inspector forwarded to `chrome://inspect`) and force-remove a leftover package. A TV that refuses an app's package id can take it under a new one, remembered per TV.
- **Doubles as a TV remote**: D-pad, playback, volume, channel, power and live typing over Samsung's own remote channel. No Developer Mode needed.
- **Tunes apps at install**: Jellyfin server + auto-login, JellyThemes/custom CSS, playback preferences, server plugins and the YouTube-trailer fix; TVapp channels; ImmiTV's Immich server + login; Camera PiP's cameras; custom launcher icons and titles.
- **Diagnoses TVs**: DUID, Tizen version, model and Developer Mode state, plus a live TV log console you can save.
- **Backup & restore**: move your settings and signing certificates to another PC, Mac or phone in one file.
- **TV-Store on the TV itself**: install [TV-Store](https://github.com/Apps2Samsung/TV-Store) from the catalog, press Arm TV-Store in the TV toolbox once, set Developer Mode's Host PC IP to the TV's own address, and the TV browses and installs catalog apps by itself, without a PC or phone. Every catalog and package it installs is checked against a signed catalog.
- **Updates itself**: in-app updater on desktop and Android, with an optional beta channel.

---

## 🚀 How It Works (Short Version)

Before you begin, ensure your Samsung TV is in Developer Mode. This is required to install apps on it.

👉 [How to enable Developer Mode on your TV](https://github.com/Apps2Samsung/Apps2Samsung/wiki/FAQ#-how-to-enable-developer-mode-on-your-tv)

1. Run the tool on your computer, or the Android or iPhone app on your phone
2. Select your Samsung TV
3. Pick an app (Jellyfin, the community catalog, or a custom `.wgt`)
4. Install

That’s it. No manual certificate handling required in most cases.

🎥 Full walkthrough:  
https://www.youtube.com/watch?v=_8mSV5pW-ic

**NixOS:** Clone the repository and run `nix-shell`; the shell environment will automatically build and launch the tool.

---

## 📚 Documentation

All detailed documentation lives in the wiki:

- 🚀 [Quick Start](../../wiki)
- ❓ [FAQ](../../wiki/FAQ)
- ⚙️ [Configuration & Jellyfin Settings](../../wiki/Configuration)
- 🔮 [Alternative Install Methods](../../wiki/Alternatives)
- 🗑️ [Uninstall / Remove](../../wiki/Remove)

> Orsay-based TVs (pre-2015 models) are no longer supported.

---

## 📦 Community Packages

Community-shared and older `.wgt` builds can be found here:  
https://github.com/Apps2Samsung/tizen-community-packages

---

## 🛠️ Support & Contributing

Contributions of all kinds are welcome, whether it’s bug reports, feature requests, code, documentation, or translations.

- Bug reports & feature requests: [Issues](../../issues)
- Ideas, feedback & questions: [Discussions](../../discussions)
- Community chat: [Discord](https://discord.gg/7mga3zh8Cv)

## 🌍 Translations

Want to help translate **Apps2Samsung**? Community translations are always appreciated.

You can contribute on Crowdin:

- [Crowdin](https://crowdin.com/project/jellyfin2samsung)

You can help by translating missing strings, improving existing translations, or reviewing your language.

Translation updates are synced back into this repository automatically.

---

## ❤️ Support the Project

If this tool helped you, consider supporting its development:

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/M4M71JOT9R)

---

## 🙏 Contributors & Thanks

This project is made possible by the people who contribute their time, knowledge, and feedback.

<a href="https://github.com/Apps2Samsung/Apps2Samsung/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Apps2Samsung/Apps2Samsung" />
</a>

Special thanks to:
- **jeppevinkel**: for providing the Jellyfin Tizen `.wgt` builds  
  https://github.com/jeppevinkel/jellyfin-tizen-builds
- **@RadicalMuffinMan**: for the Moonfin client and related work  
  https://github.com/Moonfin-Client/Smart-TV
- **@MoazSalem**: for the Litefin client and related work  
  https://github.com/MoazSalem/litefin/
- **@reisxd**: for TizenTube and related work  
  https://github.com/reisxd/TizenTube

---

## 📄 License

Apps2Samsung is released under the [MIT License](LICENSE).

Third-party components redistributed in Apps2Samsung builds (bundled binaries, Samsung CA
certificates, NuGet dependencies, fonts and app artwork) are listed in [NOTICE.md](NOTICE.md).
