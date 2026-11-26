# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

The application to SignPath Foundation is being prepared. Until it is approved and the first
signed release is out, the Windows builds are not signed. This page describes how signing works
from then on.

This policy applies to the official stable Windows builds of Apps2Samsung published at
https://github.com/Apps2Samsung/Apps2Samsung/releases.

## What is signed

- `Apps2Samsung.exe`, the Windows desktop app in the `-win-x64.zip` of a release.
- `apps2samsung-cli.exe`, the Windows command-line tool in the `Apps2Samsung.Cli-...-win-x64.zip`
  and `-win-arm64.zip` of a release.
- The Windows installer (`.msi`) that `winget install Apps2Samsung.Apps2Samsung` downloads, and
  the `Apps2Samsung.exe` inside it.

Only files built from the source code in this public repository, by its release workflow on
GitHub-hosted runners, are submitted for signing. Local builds, debug builds, pull-request
builds and files from anywhere else are never signed.

Not signed with this certificate:

- The builds for Linux, macOS, Android and iPhone.
- Third-party programs that ship inside the builds and are listed in [NOTICE.md](NOTICE.md),
  such as esbuild. They are other projects' binaries, redistributed unmodified.
- Apps from the catalog, for example those in
  [tizen-community-packages](https://github.com/Apps2Samsung/tizen-community-packages).
  They are not part of Apps2Samsung and are not built by this project. Apps2Samsung signs them
  for the TV with the user's own Samsung or Tizen certificate, which has nothing to do with the
  Windows code signing certificate.

## Team roles

| Role | Who |
| --- | --- |
| Committers and reviewers | [PatrickSt1991](https://github.com/PatrickSt1991), [kingchenc](https://github.com/kingchenc), [TemaSM](https://github.com/TemaSM), [jeppevinkel](https://github.com/jeppevinkel) |
| Approvers | [PatrickSt1991](https://github.com/PatrickSt1991) |

Committers are the accounts with write access to this repository. Changes from anyone else
arrive as pull requests and are reviewed by a committer before they are merged. Every signing
request is approved by hand by an approver.

All accounts with write access to the repository, and all accounts with access to SignPath,
use multi-factor authentication.

## Build and release process

1. Work is merged into the `beta` branch through pull requests.
2. A stable release starts when the release pull request from `beta` into `master` is merged.
3. The [Stable Release workflow](.github/workflows/stable-release.yml) builds every artifact
   from that merge commit on GitHub-hosted runners.
4. The workflow submits the Windows executables to SignPath, before anything is published.
5. An approver reviews and approves the signing request.
6. The workflow tags the commit with the version and attaches the signed files to the GitHub
   release. A request that is not approved stops the release.

## Privacy

See the [privacy policy](PRIVACY.md).

## Uninstalling

See [Uninstall](https://github.com/Apps2Samsung/Apps2Samsung/wiki/Remove) in the wiki.

## Reporting a problem

If you believe a signed Apps2Samsung file is malicious, or was not built from this repository,
open an issue at https://github.com/Apps2Samsung/Apps2Samsung/issues or write to
support@signpath.io. Affected releases are withdrawn, and the revocation of the signature is
requested where needed.
