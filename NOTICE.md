# Third-Party Notices

Apps2Samsung is licensed under the [MIT License](LICENSE), Copyright (c) 2025 Patrick Stel.

This file lists third-party material that is **redistributed inside Apps2Samsung builds**,
together with the notices those components require. It does not restrict the Apps2Samsung
license itself — it records what else is in the box.

---

## 1. Bundled executables

### esbuild 0.27.1

Prebuilt esbuild binaries ship in `Jellyfin2Samsung-CrossOS/Assets/esbuild/` for
`win-x64`, `linux-x64`, `macos-x64` and `macos-arm64`, and are invoked to bundle
injected JavaScript at install time.

> MIT License
> Copyright (c) 2020 Evan Wallace

https://github.com/evanw/esbuild — https://github.com/evanw/esbuild/blob/main/LICENSE.md

---

## 2. Samsung certificate authorities

The following Samsung-issued CA certificates are redistributed **unmodified**, solely so that
packages can be signed for and accepted by Samsung Tizen devices:

- `vd_tizen_dev_author_ca.cer`
- `vd_tizen_dev_public2.crt`
- `vd_tizen_dev_partner2.crt`
- `author_ca.cer`
- `public2.crt`

Locations: `Jellyfin2Samsung-CrossOS/Assets/TizenProfile/ca/` and `Apps2Samsung.Mobile/Resources/Raw/ca/`.

These files are the property of Samsung Electronics Co., Ltd. and are distributed as part of the
Tizen SDK / Tizen Studio. They are not covered by the Apps2Samsung MIT license, and no ownership
over them is claimed.

---

## 3. NuGet dependencies

These packages are restored at build time and are linked into published (self-contained) builds.

| Package | Version | License | Copyright |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Controls.DataGrid, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.Diagnostics | 11.3.x | MIT | AvaloniaUI OÜ |
| FluentAvaloniaUI | 2.5.1 | MIT | amwx |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | .NET Foundation and Contributors |
| Jint | 4.16.1 | BSD-2-Clause | 2013 Sebastien Ros |
| Newtonsoft.Json | 13.0.4 | MIT | James Newton-King |
| Fleck | 1.2.0 | MIT | 2010-2018 Jason Staten |
| Portable.BouncyCastle | 1.9.0 | Bouncy Castle Licence (MIT-style) | The Legion of the Bouncy Castle Inc. |
| Tmds.DBus.Protocol | 0.21.3 | MIT | Tom Deseyn |
| Microsoft.AspNetCore, Microsoft.AspNetCore.Server.Kestrel.Core | 2.3.12 | MIT | .NET Foundation and Contributors |
| Microsoft.Maui.Controls, Microsoft.Extensions.Logging.Debug, System.Security.Cryptography.Xml | — | MIT | .NET Foundation and Contributors |
| TizenSdb.Core | 1.1.3 | MIT | Patrick Stel (Apps2Samsung) |

### Fonts

`Avalonia.Fonts.Inter` embeds the **Inter** typeface by Rasmus Andersson, licensed under the
[SIL Open Font License 1.1](https://openfontlicense.org/). https://rsms.me/inter/

---

## 4. Third-party names, logos and icons

Artwork used to identify apps in the catalog and in the project's own branding remains the
property of the respective projects, and is used for identification only:

- **Jellyfin** — logo and name, https://jellyfin.org
- **Litefin** — icon, https://github.com/MoazSalem/litefin
- **TVapp** — icon, https://github.com/KaashDev/TVapp
- **Moonfin** — name, https://github.com/Moonfin-Client/Smart-TV

*Samsung*, *Tizen* and related marks are trademarks of Samsung Electronics Co., Ltd.
Apps2Samsung is not affiliated with, endorsed by, or sponsored by Samsung Electronics.

---

## 5. Independent implementations

Apps2Samsung's Samsung certificate provisioning, package signing and SDB transport are
**independent implementations written for this project**. They contain no code copied from
other Tizen tooling.

They necessarily use the same public facts as every other Tizen sideloader — Samsung's developer
certificate endpoints under `svdca.samsungqbe.com`, the SDB wire protocol on port 26101, the
`getduid` and `vd_appinstall` device commands, and the Tizen Studio interop constants required to
read and write its profile format. Those are properties of Samsung's services and file formats,
not third-party source code.

Other projects that implement the same protocols, listed for reference only — no code from either
is present in Apps2Samsung:

- [Samsung/webIDE-common-tizentv](https://github.com/Samsung/webIDE-common-tizentv) (Apache-2.0)
- [reisxd/tizen.js](https://github.com/reisxd/tizen.js) (GPL-3.0)

---

## 6. Reporting an omission

If you believe something is redistributed here without proper attribution, please open an issue at
https://github.com/Apps2Samsung/Apps2Samsung/issues and it will be corrected.
