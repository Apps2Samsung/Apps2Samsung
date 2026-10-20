#!/usr/bin/env python3
"""Write the SideStore source for the iPhone app (issue #753 step 6).

SideStore (and AltStore, whose source format it reads) installs an app from a "source": a JSON
file listing the app and where its IPA is. Ours lives at https://apps2samsung.com/sidestore/apps.json,
served from docs/ on `beta` the way the F-Droid repo is, and points at the unsigned IPA of the
latest stable release. Betas are not in it: they are a manual IPA import.

Everything that describes the app comes from the repo, so the source can't drift from the build:
  * bundle id and minimum iOS version   Apps2Samsung.Mobile/Apps2Samsung.Mobile.csproj
  * the local-network permission text   Apps2Samsung.Mobile/Platforms/iOS/Info.plist
  * name, subtitle and description      fastlane/metadata/android/en-US (the F-Droid listing)
The release (date, notes, IPA url and size) comes from `gh release view --json`, and the version
and build number from the IPA's own Info.plist: SideStore matches both against the installed app,
so they must be what the IPA carries (three-part version, versionCode build, apple-version.sh),
not the release tag (a tag like v2.3.1.1 is not a version iOS accepts).

Both the current `versions` list and the older flat fields (version, versionDate, downloadURL,
size) are written: older SideStore/AltStore builds only read the flat ones.

Usage: .github/scripts/sidestore-source.py <release.json> <ipa> <output apps.json>
       release.json = gh release view <tag> --json tagName,publishedAt,body,assets
       ipa          = that release's "-ios-unsigned.ipa" asset, downloaded
Exits non-zero when the release has no "-ios-unsigned.ipa" asset, the IPA has no app Info.plist
or a repo field is missing.
"""

from __future__ import annotations

import json
import plistlib
import re
import sys
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CSPROJ = REPO_ROOT / "Apps2Samsung.Mobile/Apps2Samsung.Mobile.csproj"
INFO_PLIST = REPO_ROOT / "Apps2Samsung.Mobile/Platforms/iOS/Info.plist"
LISTING = REPO_ROOT / "fastlane/metadata/android/en-US"

SITE = "https://apps2samsung.com"
IPA_SUFFIX = "-ios-unsigned.ipa"
# The brand teal (Apps2Samsung.Core/Design/DesignTokens.cs), without the '#' as the format wants.
TINT = "10B981"


def fail(message: str) -> None:
    print(f"❌ {message}", file=sys.stderr)
    sys.exit(1)


def csproj_value(text: str, pattern: str, what: str) -> str:
    match = re.search(pattern, text)
    if not match:
        fail(f"{what} not found in {CSPROJ.relative_to(REPO_ROOT)}")
    return match.group(1).strip()


def ipa_versions(ipa_path: Path) -> tuple[str, str]:
    """CFBundleShortVersionString and CFBundleVersion of the app inside the IPA."""
    with zipfile.ZipFile(ipa_path) as ipa:
        name = next((n for n in ipa.namelist() if re.fullmatch(r"Payload/[^/]+\.app/Info\.plist", n)), None)
        if name is None:
            fail(f"{ipa_path} has no Payload/<app>.app/Info.plist")
        info = plistlib.loads(ipa.read(name))
    version = info.get("CFBundleShortVersionString")
    build = info.get("CFBundleVersion")
    if not version or not build:
        fail(f"{ipa_path}: CFBundleShortVersionString or CFBundleVersion missing")
    return str(version), str(build)


def main() -> None:
    if len(sys.argv) != 4:
        fail("usage: sidestore-source.py <release.json> <ipa> <output apps.json>")
    release = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    version, build = ipa_versions(Path(sys.argv[2]))
    output = Path(sys.argv[3])

    csproj = CSPROJ.read_text(encoding="utf-8")
    bundle_id = csproj_value(csproj, r"<ApplicationId>([^<]+)</ApplicationId>", "ApplicationId")
    min_ios = csproj_value(
        csproj,
        r"<SupportedOSPlatformVersion Condition=\"[^\"]*== 'ios'\">([^<]+)</SupportedOSPlatformVersion>",
        "the iOS SupportedOSPlatformVersion",
    )

    with INFO_PLIST.open("rb") as handle:
        plist = plistlib.load(handle)
    local_network = plist.get("NSLocalNetworkUsageDescription")
    if not local_network:
        fail(f"NSLocalNetworkUsageDescription not found in {INFO_PLIST.relative_to(REPO_ROOT)}")

    title = (LISTING / "title.txt").read_text(encoding="utf-8").strip()
    subtitle = (LISTING / "short_description.txt").read_text(encoding="utf-8").strip()
    description = (LISTING / "full_description.txt").read_text(encoding="utf-8").strip()

    ipa = next((a for a in release.get("assets", []) if a["name"].endswith(IPA_SUFFIX)), None)
    if ipa is None:
        fail(f"release {release.get('tagName')} has no *{IPA_SUFFIX} asset (is IOS_PUBLISH on?)")

    # The release's publish date, or the asset's when the release was re-run afterwards.
    date = release.get("publishedAt") or ipa.get("updatedAt") or ipa["createdAt"]
    # The notes up to the release's platform table: the table and the antivirus notice under it
    # are about the desktop downloads, not this app.
    notes = (release.get("body") or "").replace("\r\n", "\n")
    notes = notes.split("\n| Platform |", 1)[0].strip()
    icon = f"{SITE}/sidestore/icon.png"

    app = {
        "name": title,
        "bundleIdentifier": bundle_id,
        "developerName": "MadeByPatrick",
        "subtitle": subtitle,
        "localizedDescription": description,
        "iconURL": icon,
        "tintColor": TINT,
        "category": "utilities",
        "screenshots": [],
        "versions": [
            {
                "version": version,
                "buildVersion": build,
                "date": date,
                "localizedDescription": notes,
                "downloadURL": ipa["url"],
                "size": ipa["size"],
                "minOSVersion": min_ios,
            }
        ],
        "appPermissions": {
            "entitlements": [],
            "privacy": {"NSLocalNetworkUsageDescription": local_network},
        },
        # Older clients read these instead of `versions`.
        "version": version,
        "versionDate": date,
        "versionDescription": notes,
        "downloadURL": ipa["url"],
        "size": ipa["size"],
    }

    source = {
        "name": title,
        "identifier": f"{bundle_id}.source",
        "subtitle": subtitle,
        "description": "The iPhone app of Apps2Samsung, from its GitHub stable releases.",
        "iconURL": icon,
        "website": SITE,
        "tintColor": TINT,
        "featuredApps": [bundle_id],
        "apps": [app],
        "news": [],
    }

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(source, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"✅ {output}: {title} {version} ({build}) from {release['tagName']}, {ipa['name']} ({ipa['size']} bytes)")


if __name__ == "__main__":
    main()
