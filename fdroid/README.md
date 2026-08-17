# Apps2Samsung F-Droid repository

A **self-hosted, F-Droid-compatible** repository for the Apps2Samsung Android app. The
official F-Droid buildserver can't build .NET MAUI, and IzzyOnDroid declined the app on
AI-usage grounds — so we host our own repo. It's built from the project's **signed stable
release APK** and published to GitHub Pages.

- **Repo URL (add this in the F-Droid app):** `https://apps2samsung.com/fdroid/repo`
- Served from `docs/fdroid/repo` on the `beta` branch (GitHub Pages, `apps2samsung.com`).
- Rebuilt automatically by `.github/workflows/fdroid-repo.yml` after every successful
  **Stable Release** (or on manual `workflow_dispatch`). Only the latest APK is kept.

## One-time setup: the repo signing key

An F-Droid repo signs its *index* with a keystore that is **separate** from the app's
signing key. Generate it once (locally), then add it to the repo secrets.

```bash
keytool -genkeypair -v \
  -keystore fdroid-repo.p12 -storetype PKCS12 \
  -alias apps2samsung -keyalg RSA -keysize 4096 -validity 10000 \
  -dname "CN=Apps2Samsung, O=Apps2Samsung"
# choose a password when prompted; use the SAME value for both secrets below (PKCS12
# uses one password for the store and the key).

base64 -w0 fdroid-repo.p12    # copy the output for FDROID_KEYSTORE_BASE64
```

Add these **repository secrets** (Settings → Secrets and variables → Actions):

| Secret | Value |
|--------|-------|
| `FDROID_KEYSTORE_BASE64` | the `base64 -w0 fdroid-repo.p12` output |
| `FDROID_KEYSTORE_PASS`   | the keystore password you chose |
| `FDROID_KEY_ALIAS`       | `apps2samsung` |
| `FDROID_KEY_PASS`        | same as `FDROID_KEYSTORE_PASS` (PKCS12) |

⚠️ Keep `fdroid-repo.p12` and its password safe and **reuse the same keystore forever** —
if it changes, existing users' F-Droid clients reject the repo and must re-add it.

Once the secrets are set, run the **F-Droid Repo** workflow manually once
(Actions → F-Droid Repo → Run workflow) to publish the first index; after that it runs on
each stable release.

## For users

In the F-Droid app (or Droid-ify / Neo Store): **Settings → Repositories → Add**, paste
`https://apps2samsung.com/fdroid/repo`, then install Apps2Samsung. Updates arrive
automatically whenever a new stable is published.
