# UE55TU7092UXXH: installing the local Vetro port on Tizen 5.5

Owner-verified target: **UE55TU7092UXXH**, firmware **T-KTSU2DEUC-2743.0**,
build **T-KTSU2DEUC-REL-202607022000**, Tizen 5.5, ARMv7. This note records
installation/signing requirements and the boundary between installing an app
and obtaining root. It does not add a new exploit or change the installer.

## What works and what needs a separate port

| Item | Result on this target |
| --- | --- |
| Samsung developer certificate and custom WGT installation | Used successfully with an owner-held Partner signing profile |
| Stock Vetro 0.5.0 root/launcher | Insufficient for this TV's older runtime |
| Locally adapted Vetro 0.5.0 | Launcher, app names, direct Settings and cold-boot return confirmed |
| TizenTube 2.1.1 `TizenTubeOld.wgt` | Clean reinstall completed; owner confirmed video and sound |
| Root access | Separate owner-operated Remote PC/CIFS bootstrap and DeviceTracker boot hook |

**An app installing successfully is not a root proof.** A catalog root badge
does not mean every model can use that app's root route. Reinstalling stock
Vetro over the custom build may remove the compatibility changes.

## Prepare the TV and certificate

1. Check the full model, firmware and build. Matching just “TU7000” or “Tizen
   5.5” does not establish compatibility with this port.
2. Enable Developer Mode in Samsung Apps/App Settings using `12345`, set
   **Host PC IP to the installing computer's LAN address**, and restart the TV.
   Keep this address for the TU7092 external-agent route. Vetro's loopback-IP
   instructions apply to its stock TV-local archive route instead.
3. Connect with Apps2Samsung. Authorize **this TV's DUID** in the signing
   profile; do not copy another person's distributor certificate or password.
4. Use the privilege level required by the package. The reported setup used
   a Partner profile. Keep the same author certificate for app updates; a
   distributor profile authorizing a different TV is not enough on its own.

See [Samsung's device workflow](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html)
and the [Apps2Samsung FAQ](https://github.com/Apps2Samsung/Apps2Samsung/wiki/FAQ).

## Build and install Vetro

Use the companion Vetro contribution's `rooting/ports/tu7092/README.md` and
source patches, pinned to Vetro 0.5.0 commit
`81e16b164236e2fd1edae50a06d986da5e607469`. Until that contribution is merged,
use its proposed branch/files; this note does not claim the catalog already
contains a compatible build.

The port builds the launcher against .NET 3.0 and invokes the TV's existing
signed Samsung host. It preserves authenticated root-agent traffic on Node
4.4.3. It requires the independently established root worker described in the
companion samsung-tv-root contribution,
`docs/research-notes/TU7092_PERSISTENT_ROOT.md`.

In Apps2Samsung, choose the resulting **local/custom WGT**, sign it for your
TV and install it. Open Vetro once. Do not substitute the stock catalog WGT
when testing this port. Confirm the package version and start of its service;
then verify the root agent before enabling Launcher.

The current CLI also documents local WGT installation/signing in
`apps2samsung-cli --help` and `sign --help`. Its command syntax is an additional
installation option, not a separate end-to-end CLI test on this TV. The installer
version used in the original hardware session was not captured, so this report
does not attribute that test to a newer Apps2Samsung release.

## Verify after installation and after cold boot

- Vetro reports an authenticated agent with UID/GID 0 and
  `System::Privileged`, rather than only `prepared: true`.
- The launcher is enabled and running with no saved error. Press physical Home.
- Test the apps you use. TizenTube's tested older-runtime asset was
  [2.1.1 TizenTubeOld.wgt](https://github.com/reisxd/TizenTube/releases/tag/v2.1.1);
  no claim is made here that it remains the newest release.
- After unplug/restart, allow about a minute. One verified boot started the
  launcher at uptime 49.79 s. A short initial absence is not proof root was lost.
- A changed matching boot ID and root readback establish boot recovery;
  seeing the installed Vetro tile alone does not.

## Troubleshooting

`EACCES` on `vetro-dock-apps.tsv` was caused by an earlier root-owned mode-600
list. The Vetro port guide describes a narrow backed-up ownership repair.
Do not recursively relax `/home/owner` permissions or change signing material.

Some legacy SDB workflows print a successful transfer without completing the
app registration step. Check that Apps2Samsung reports package installation
completed and that the TV lists/launches the app. A standalone `sdb push` log
is not enough evidence. No installer-code fix is claimed from this observation.

A firmware update, reset or replacement package can remove the custom setup.
The companion rooting guide includes per-TV backups, stop flags and rollback.
Do not upload firmware images, DeviceTracker script bodies, keys, serial/MAC/
DUID values, account details or raw private logs with a compatibility report.

This documentation was AI-assisted and checked against the owner's observed
device results. Another physical TV and the latest installer binary were not
tested as part of this contribution.
