#!/usr/bin/env python3
"""Stamp the beta marker into the version the app shows at runtime (issue #578).

The `beta` branch keeps a clean version (e.g. `v2.7.9`) because the Stable Release workflow
refuses to build a version that still carries `-beta`. A beta build should still tell the user
it is a beta, so the Beta Pre-Release workflow runs this script on its checkout right before
building: the marker is added to the working tree only and is never committed.

Stamped:
  AppSettings.AppVersion   ->  "v2.7.9-beta"  (desktop footer + desktop update check)

Not stamped, on purpose:
  * the Android versionName — the workflow passes it on the command line
    (`-p:ApplicationDisplayVersion=<version>-beta`), which overrides the .csproj anyway;
  * the macOS Info.plist (CFBundle*Version) and the .deb control `Version`, which are
    packaging fields that want a plain numeric version.

The beta marker is cosmetic for update checks: GitHubUpdateChecker.CleanVersionString() drops
any `-suffix` before comparing, so a running `v2.7.9-beta` is neither newer nor older than the
`v2.7.9-beta` release it came from.

Idempotent: a version that already ends in the suffix is left alone. Exits non-zero when a
target file or its version line is missing, so a rename can't silently ship an unmarked beta.

Usage: .github/scripts/stamp-beta-version.py [suffix]    # suffix defaults to "beta"
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# (path relative to the repo root, pattern, what the stamped version is used for).
# Each pattern must capture: prefix, version (numeric core), suffix (any existing -marker), tail.
TARGETS = [
    (
        "Jellyfin2Samsung-CrossOS/Helpers/AppSettings.cs",
        re.compile(
            r'(?P<prefix>AppVersion\s*\{\s*get;\s*set;\s*\}\s*=\s*")'
            r'(?P<version>v?\d+(?:\.\d+){1,3})'
            r'(?P<suffix>[^"]*)'
            r'(?P<tail>")'
        ),
        "desktop footer + desktop update check",
    ),
]


def stamp(relative_path: str, pattern: re.Pattern[str], usage: str, suffix: str) -> None:
    path = REPO_ROOT / relative_path
    if not path.is_file():
        sys.exit(f"::error::{relative_path} not found — update {Path(__file__).name}")

    # newline="" keeps the file's own line endings (and its BOM) byte-for-byte, so the only
    # change in the build tree is the version string itself.
    with open(path, encoding="utf-8", newline="") as handle:
        original = handle.read()
    match = pattern.search(original)
    if match is None:
        sys.exit(f"::error::no version line matched in {relative_path} — update {Path(__file__).name}")

    marker = f"-{suffix}"
    if match.group("suffix") == marker:
        print(f"= {relative_path}: already {match.group('version')}{marker} ({usage})")
        return

    stamped = f"{match.group('prefix')}{match.group('version')}{marker}{match.group('tail')}"
    with open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(original[: match.start()] + stamped + original[match.end() :])
    print(f"✔ {relative_path}: {match.group('version')}{match.group('suffix')} -> "
          f"{match.group('version')}{marker} ({usage})")


def main() -> None:
    suffix = (sys.argv[1] if len(sys.argv) > 1 else "beta").lstrip("-")
    if not suffix:
        sys.exit("::error::empty version suffix")

    for relative_path, pattern, usage in TARGETS:
        stamp(relative_path, pattern, usage, suffix)


if __name__ == "__main__":
    main()
