# Jellyfin plugins in the packaged app

The desktop installer copies the client scripts of the plugins below into the Jellyfin app at install
time (Jellyfin settings, "Download Jellyfin plugins"), transpiled to ES2015 by the bundled esbuild. The phone app
doesn't: there is no esbuild on Android or iOS, so `UseServerScripts` is off there.

Rules that apply to every plugin:

- **No raw plugin code on the TV.** A file esbuild can't transpile is left out of the app, with a ⛔
  line in the log (`EsbuildHelper.TranspileAsync` returns null).
- **Tizen 4.0 and newer.** ES2015 needs Chromium 56 (Tizen 4). Tizen 3 (Chromium 47) lacks parts of it
  and esbuild can't go lower, so below 4.0 no plugins are injected
  (`Constants.TizenVersions.PluginScriptsMin`).
- **The server's copy first.** Where the plugin serves its own client script, that copy is used, so it
  matches the plugin version on the server. GitHub is a fallback, at the installed version's release tag.
- **Plugin detection needs an admin account.** `/Plugins` is admin-only; with a normal account no
  server plugin is detected. KefinTweaks is found in JS Injector's public `public.js` instead.

## Supported versions

"From" is the oldest plugin version the app supports. Versions below a `MinimumVersion` are skipped at
install. The Jellyfin columns list the plugin versions released for that Jellyfin version, taken from
the plugins' release manifests and GitHub releases (2026-10-20).

| Plugin | From | Jellyfin 10.10 | Jellyfin 10.11 | Jellyfin 12 | How the app gets it |
|---|---|---|---|---|---|
| Jellyfin Enhanced | **9.0.0.0** (`MinimumVersion`) | 9.0 – 10.11.1 | 9.0 – 12.12 | 12.0 – 12.12 | `/JellyfinEnhanced/script` plus its modules: the `allComponentScripts` list (9.0 – 12.9) or `/JellyfinEnhanced/bundle.js` (12.10+). Before 9.0 the modules are in an unnamed list the patch can't read. |
| Editor's Choice | 1.0 | – | 1.5.x | 1.6.x | `/EditorsChoice/script`, embedded in a loader. |
| Media Bar | any | 2.3 – 2.4.11 | 2.4.x | 3.0+ | `/MediaBar/slideshowpure.{js,css}` from 2.4.13.0; older versions from jsDelivr at their release tag. With File Transformation the server's own tag is used. |
| Home Screen Sections | any | 2.x | 2.4 – 3.0 | 3.0+ | `/HomeScreen/home-screen-sections.{js,css}` from 2.2.5.0; older versions from GitHub at their release tag. |
| NotifySync | **6.0.0.0** (`MinimumVersion`) | – | – (10.11 builds end at 5.8.4) | 6.0+ | `/NotifySync/client.js`. Before 6.0 the client fetched relative to the page origin, which in the app isn't the server. |
| Media Carousel | **4.4.2.0** (`MinimumVersion`) | – | – (10.11 builds end at 3.15.1) | 4.4.2+ | `/MediaCarousel/media-carousel.js`. Before 4.4.2 it didn't build its addresses with `ApiClient.getUrl`. |
| KefinTweaks | v0.1 | any | any | any | JS Injector user script: roots read from `public.js`, every module downloaded from that root (fallback v0.5.2) and redirected to the local copy. `scripts/games.js` is left out (inline scripts in fetched HTML). |

Known open points:

- **Home Screen Sections 3.x** also patches jellyfin-web's `loadSections` on the server, which the TV's
  own jellyfin-web never gets. Whether its sections show on the TV needs a check on a TV or the emulator.
- **KefinTweaks v0.5.2** lists `scripts/homeScreenConfig.js`, which isn't in its repository (a 404 in
  the browser too).

## Tests

- `tools/PluginPatchRig` runs the real `JellyfinPluginPatcher` against a fake server for each case in
  `cases.json` (a plugin at a release tag, served from a clone of its repository) and checks which
  files end up in the app, that skipped versions stay out, and which URLs were fetched. Everything it
  caches is then parsed as ES2015.
- `tools/plugin-es2015-check` runs the bundled esbuild over the current upstream sources of every
  plugin daily, so a plugin update that breaks the transpile shows up before users install it.

Both run in `.github/workflows/plugin-es2015-check.yml`. When a plugin changes how it loads its
scripts, or a new version needs a `MinimumVersion`, add a case to `cases.json` and update this table.
