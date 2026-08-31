#!/usr/bin/env python3
"""
Builds the body of a release from a curated file when there is one, and from GitHub's generated
notes when there isn't.

Both release workflows used to compose the body inline, from generate-notes alone. That output is
a flat list of pull request titles, which is fine for a routine build and no substitute for the
notes a real release gets: what changed, and why anyone should care. Hand-written notes were
applied to the release afterwards, and the next build of the same version overwrote them - the
comment at the top of beta-prerelease.yml records that happening.

So the curated notes live in the repo instead, at .github/release-notes/<version>.md, and a build
prefers them. Rebuild the same version as often as you like; the notes come back. Ship a version
with no file and the generated list is used, which is the old behaviour and still perfectly good
for a beta nobody is announcing.

The file holds the middle of the page only - the summary and the sections. The heading, the
downloads table, the security notice and the full-changelog link are added here so a beta and a
stable release of the same content stay consistent with each other.

  build_release_notes.py --tag v2.7.9-beta --channel beta --generated CHANGELOG.md
"""
import argparse
import re
import subprocess
import sys
from pathlib import Path

CURATED = Path(".github/release-notes")

DOWNLOADS = {
    "stable": [
        ("🍎 macOS (.app + dmg)", "✅ Stable", "ARM64 + Intel"),
        ("🍎 macOS (CLI)", "✅ Stable", "Per-arch tar.gz"),
        ("🐧 Linux", "✅ Stable", "x64 + ARM64 (tar.gz / .deb / .rpm / AppImage)"),
        ("🪟 Windows", "✅ Stable", "CI-built"),
        ("🤖 Android (.apk)", "✅ Stable", "Sideload; phone as installer head"),
    ],
    "beta": [
        ("🍎 macOS (.app + dmg)", "⚠️ Beta", "ARM64 + Intel"),
        ("🍎 macOS (CLI)", "⚠️ Beta", "Per-arch tar.gz"),
        ("🐧 Linux", "⚠️ Beta", "x64 + ARM64 (tar.gz / .deb / .rpm / AppImage)"),
        ("🪟 Windows", "⚠️ Beta", "CI-built"),
        ("🤖 Android (.apk)", "⚠️ Beta", "Sideload; phone as installer head"),
    ],
}


def base_version(tag):
    """v2.7.9-beta and v2.7.9 are the same release, written up once."""
    return re.sub(r"-beta.*$", "", tag)


def tidy(generated):
    """GitHub's generated notes, minus the parts that read as machinery: the HTML comment it opens
    with, the redundant heading, and the "by @user in <url>/pull/N" tail on every line."""
    out = []
    for line in generated.splitlines():
        if line.startswith("<!--") or re.fullmatch(r"## What's Changed\s*", line):
            continue
        line = re.sub(
            r"^\* (.*) by @[^ ]+ in https?://github\.com/[^ ]+/pull/(\d+)\s*$", r"- \1 (#\2)", line)
        if line.strip() or (out and out[-1].strip()):
            out.append(line)
    return "\n".join(out).strip()


def previous_tag(tag, channel):
    """The tag a reader would compare against: the last stable for a stable release, the previous
    beta for a beta."""
    try:
        tags = subprocess.run(["git", "tag"], capture_output=True, text=True,
                              check=True).stdout.split()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None

    if channel == "stable":
        candidates = [t for t in tags if re.fullmatch(r"v\d+(\.\d+){1,3}", t) and t != tag]
    else:
        candidates = [t for t in tags if "beta" in t and t != tag]
    if not candidates:
        return None
    return sorted(candidates, key=lambda t: [int(n) for n in re.findall(r"\d+", t)])[-1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", required=True)
    parser.add_argument("--channel", required=True, choices=("beta", "stable"))
    parser.add_argument("--generated", type=Path)
    parser.add_argument("--date", required=True)
    parser.add_argument("--repo", default="Apps2Samsung/Apps2Samsung")
    args = parser.parse_args()

    curated = CURATED / f"{base_version(args.tag)}.md"
    if curated.exists():
        body = curated.read_text(encoding="utf-8").strip()
        print(f"Using curated notes from {curated}", file=sys.stderr)
    elif args.generated and args.generated.exists():
        body = tidy(args.generated.read_text(encoding="utf-8"))
        print(f"No {curated} - using the generated notes", file=sys.stderr)
    else:
        body = "_No release notes for this build._"
        print(f"No {curated} and nothing generated", file=sys.stderr)

    rows = "\n".join(f"| {platform} | {status} | {note} |"
                     for platform, status, note in DOWNLOADS[args.channel])

    page = [f"## 📦 {args.tag} — {args.date}", "", body, "", "---", "",
            "### 📥 Downloads", "",
            "| Platform | Status | Notes |", "|----------|--------|-------|", rows, "",
            "---", "",
            "### 🛡️ Security notice", "",
            "Antivirus warnings may occur and are likely **false positives** — the binaries are "
            "unsigned and built in the open by GitHub Actions.", ""]

    since = previous_tag(args.tag, args.channel)
    if since:
        page.append(f"**Full changelog:** https://github.com/{args.repo}/compare/{since}...{args.tag}")

    print("\n".join(page))
    return 0


if __name__ == "__main__":
    sys.exit(main())
