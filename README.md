# Jellyfin2Samsung

<p align="center">
  <img src=".github/jellyfin-tizen-logo.svg" width="220" />
</p>
<p align="center">
  <img src="https://img.shields.io/github/v/release/Jellyfin2Samsung/Samsung-Jellyfin-Installer?label=stable&style=for-the-badge" />
  <img src="https://img.shields.io/github/v/release/Jellyfin2Samsung/Samsung-Jellyfin-Installer?include_prereleases&label=beta&style=for-the-badge" />
  <img src="https://img.shields.io/badge/Tizen-TV-blue?style=for-the-badge" />
  <img src="https://img.shields.io/badge/OS-Windows%20%7C%20Linux%20%7C%20macOS-brightgreen?style=for-the-badge" />
  <a href="https://discord.gg/7mga3zh8Cv">
    <img src="https://img.shields.io/badge/Discord-Community-7289DA?style=for-the-badge&logo=discord&logoColor=white" />
  </a>
</p>

<p align="center">
  <b>Jellyfin 2 Samsung</b> is a small cross-platform tool that helps you install <a href="https://jellyfin.org">Jellyfin</a> on <b>Samsung Smart TVs running Tizen OS</b>.
  <br/>
  It handles device detection, certificates, and installation so you don’t have to fight with Tizen Studio or manual sideloading.
  <br/><br/>
  🌐 Available in: Danish, Dutch, English, French, German, Portuguese, Turkish
  <br/>
  🇩🇰 🇳🇱 🇬🇧 🇫🇷 🇩🇪 🇵🇹 🇹🇷 
</p>

---

## 📦 Current Versions

<!-- versions:start -->

| Channel    | Version                                                             | Notes                        |
|------------|---------------------------------------------------------------------|------------------------------|
| **Stable** | [v2.3.1.1](https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer/releases/tag/v2.3.1.1)                                        | Recommended for most users   |
| **Beta**   | [N/A](#)                                            | Includes new features        |

<!-- versions:end -->

👉 All releases: https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer/releases

---

## 🚀 How It Works (Short Version)

Before you begin, ensure your Samsung TV is in Developer Mode. This is required to install applications like Jellyfin.

👉 [How to enable Developer Mode on your TV](https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer/wiki/FAQ#-how-to-enable-developer-mode-on-your-tv)

1. Run the tool on your computer
2. Select your Samsung TV
3. Pick a Jellyfin version
4. Install

That’s it. No manual certificate handling required in most cases.

🎥 Full walkthrough:  
https://www.youtube.com/watch?v=_8mSV5pW-ic

**NixOS:** Clone the [`Samsung2Jellyfin` branch](https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer.git) and run `nix-shell` — the shell environment will automatically build and launch the tool.

---

## 📚 Documentation

All detailed documentation lives in the wiki:

- 🚀 [Quick Start](../../wiki)
- ❓ [FAQ](../../wiki/FAQ)
- ⚙️ [Configuration & Jellyfin Settings](../../wiki/Configuration)
- 🔮 [Alternative Install Methods](../../wiki/Alternatives)
- 🗑️ [Uninstall / Remove](../../wiki/Remove)

---

## ⚠️ Older Samsung TVs (Orsay OS)

Samsung TVs running **Orsay OS** are not supported.

Use this installer instead:  
https://github.com/PatrickSt1991/Jellyfin-Orsay-Installer

---

## 📦 Community Packages

Community-shared and older `.wgt` builds can be found here:  
https://github.com/PatrickSt1991/tizen-community-packages

---

## 🛠️ Support & Contributing

Contributions of all kinds are welcome — whether it’s bug reports, feature requests, code, documentation, or translations.

- Bug reports & feature requests: [Issues](../../issues)
- Ideas, feedback & questions: [Discussions](../../discussions)
- Community chat: [Discord](https://discord.gg/7mga3zh8Cv)

## 🌍 Translations

Want to help translate **Jellyfin2Samsung**? Community translations are always appreciated.

You can contribute here:

- [Transifex](https://app.transifex.com/madebypatrick/jellyfin2samsung)
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

<a href="https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Jellyfin2Samsung/Samsung-Jellyfin-Installer" />
</a>

Special thanks to:
- **jeppevinkel** — for providing the Jellyfin Tizen `.wgt` builds  
  https://github.com/jeppevinkel/jellyfin-tizen-builds
- **@RadicalMuffinMan** — for the Moonfin client and related work  
  https://github.com/Moonfin-Client/Smart-TV
- **@MoazSalem** — for the Litefin client and related work  
  https://github.com/MoazSalem/litefin/

