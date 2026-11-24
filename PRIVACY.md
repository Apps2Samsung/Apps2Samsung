# Privacy policy

Apps2Samsung has no accounts of its own, no telemetry, no analytics and no crash reporting.
The project runs no server that receives anything about you, your TVs or what you install.

This page lists every connection the desktop app, the phone apps and `apps2samsung-cli` make,
so you can check that for yourself in the [source code](https://github.com/Apps2Samsung/Apps2Samsung).

## What stays on your device

- Settings, signing certificates and their passwords, saved TV lists and logs are kept on your
  device. Where exactly is listed under [Uninstall](https://github.com/Apps2Samsung/Apps2Samsung/wiki/Remove)
  in the wiki.
- Copies of your certificates and of each TV's app list are written to your own Documents
  folder (Downloads on Android, Files on iPhone), so that they survive a reinstall.
- A log or a backup leaves the device only when you copy, share or export it yourself. A backup
  file holds your settings and certificates, including any passwords and API keys you typed.
  It is not encrypted: keep it private.
- Settings you type for an app (a server address, a login, an API key) are stored on your
  device and written into the app that is installed on your TV. On the desktop they are kept
  in a plain settings file in your user profile.
- On a phone, the system's own backup (Google or iCloud) may include the app's data, if you
  have that turned on.

## Connections the app makes by itself

These are plain downloads. They carry no account, no device id and nothing about your TVs.
As with any download, the host sees your IP address.

| When | Where | What for |
| --- | --- | --- |
| At start, then every 15 minutes while the app is open | `apps2samsung.com` (the project's site, hosted on GitHub Pages) | The app catalog and the public download counts |
| At start | `github.com`, `api.github.com`, `raw.githubusercontent.com` | The update check, the catalog's list of sources, and the release lists of catalog apps when the catalog file is out of date |
| When the catalog is shown | The hosts the catalog names for app icons, mostly `apps2samsung.com` and GitHub | App icons |
| At start | Sources and repositories you added yourself | Their app lists |

If you set a GitHub token in Settings, or have one in the `GITHUB_TOKEN` environment variable
or the GitHub CLI, the desktop app sends it to GitHub with its requests, to get past GitHub's
rate limit.

## Your local network

- The app looks for TVs by trying to connect to every address on your own network, on the
  TV's developer port (26101) and its control port (8001). Nothing about what it finds leaves
  your network.
- It talks to the TV you pick: to install and remove apps, read its information, act as a
  remote control and wake it up. With TV-Store on the TV it also talks to TV-Store there.
- While you sign in to Samsung, and while the TV logs window is open, the app listens on a
  port on your device to receive the answer.

## Connections you start

| What you do | Where | What is sent |
| --- | --- | --- |
| Sign in to Samsung to make a certificate | `account.samsung.com` and Samsung's certificate service (`svdca.samsungqbe.com`) | You sign in on Samsung's own page. Samsung then gets the certificate requests, which hold your Samsung account's email address and the device ids of the TVs the certificate is for. |
| Install or download an app | The host of that app's files: mostly GitHub, otherwise whatever the app's source names | The download. GitHub counts downloads per file; those public counts are where "Most downloaded" comes from. |
| Open an app's details or build information | GitHub and the image hosts the catalog names | Text and screenshots |
| Show the icons of the apps on your TV | GitHub, `rs.ltd` and Samsung's icon server | A list of Samsung app ids and the icons |
| Set up Jellyfin, Immich or another app that needs your own server | The server address you typed | What that server needs: your login for it, and for Jellyfin the app's name and version, your computer's name and an id derived from it |
| Install Jellyfin with plugins that your server does not serve itself | `cdn.jsdelivr.net`, `raw.githubusercontent.com` | The download of those plugin files |
| Renew an expired Samsung certificate authority, after the app asked | `download.tizen.org` | The download |
| Click a link (the site, GitHub, Crowdin, Discord, Reddit, Ko-fi) | Your browser opens it | Nothing from the app. "Add an app" opens GitHub with only what you typed in the form. |

Your Samsung password is typed on Samsung's own page; Apps2Samsung does not read or store it.
The sign-in itself is held in memory while the certificate is made and is not saved. On a
phone the sign-in page may remember you, as a browser would. Nothing from the Samsung sign-in
is sent anywhere but to Samsung.

## Other parties

These services have their own privacy policies: [GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement),
[Samsung account](https://account.samsung.com/membership/policy/privacy),
[jsDelivr](https://www.jsdelivr.com/terms/privacy-policy-jsdelivr-net).
Apps from the catalog are other projects' software. What they do on your TV is up to them.

## Turning things off

- The update check at start: set `CheckForUpdatesOnStartup` to `false` in the desktop app's
  `settings.json`.
- Everything else under "Connections the app makes by itself" is what fills the catalog. The
  app has no switch for it.

## Questions

Open an issue at https://github.com/Apps2Samsung/Apps2Samsung/issues.
