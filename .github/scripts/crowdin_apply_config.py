#!/usr/bin/env python3
"""
Writes the export pattern from crowdin.yml onto the Crowdin file itself, and clears the project's
per-language mapping.

The export path is decided TWICE and only one of them is in this repo. Crowdin resolves its own
file setting when it builds the archive; crowdin.yml only tells the CLI where to put what it finds
inside. They drifted, and the drift was invisible: the server pattern was %two_letters_code%, so
pt-BR and pt-PT both became pt.json, Crowdin kept one and dropped the other language from the
build, and crowdin.yml's languages_mapping - written precisely to prevent that - never had
anything to act on. pt-PT and zh-CN had never once reached this repo.

Naming every file by %locale% removes the collision by construction rather than one pair at a
time, which is why the project's languageMapping goes too: it existed only to patch the two pairs
that had already collided, and a new language would have needed another entry.

It also removes orphaned source files. Crowdin keeps a file that was uploaded under a different
path or before the branch existed, and that file keeps exporting: the project held three named
en.json - the live one and two left from earlier layouts - so every build carried all three and a
29-language export came back with 56 entries. Whichever the API happens to list first is then a
coin toss for any tool that looks a file up by name.

Run it after changing the translation pattern in crowdin.yml. Idempotent - it reports "already
correct" and writes nothing when the two sides agree.

  CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN in the environment; --apply to write.
"""
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

API = "https://api.crowdin.com/api/v2"
CONFIG = Path("crowdin.yml")
SOURCE_FILE = "en.json"
BRANCH = "beta"

TOKEN = os.environ.get("CROWDIN_PERSONAL_TOKEN", "")
PROJECT = os.environ.get("CROWDIN_PROJECT_ID", "")


def call(path, method="GET", body=None, **params):
    url = f"{API}{path}" + ("?" + urllib.parse.urlencode(params) if params else "")
    request = urllib.request.Request(
        url, method=method,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Authorization": f"Bearer {TOKEN}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request) as response:
            payload = response.read()
        return json.loads(payload) if payload else {}
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {url} -> HTTP {e.code}: {e.read().decode(errors='replace')[:400]}")


def listing(path, **params):
    out, offset = [], 0
    while True:
        page = call(path, limit=500, offset=offset, **params)["data"]
        out += [row["data"] for row in page]
        if len(page) < 500:
            return out
        offset += 500


def wanted_pattern():
    """The translation pattern from crowdin.yml. Read with a regex rather than a YAML parser so the
    job needs nothing installed beyond the interpreter."""
    match = re.search(r"^\s*translation:\s*(\S+)\s*$", CONFIG.read_text(encoding="utf-8"),
                      re.MULTILINE)
    if not match:
        raise SystemExit(f"No 'translation:' line in {CONFIG}.")
    return match.group(1).strip('"\'')


def main():
    if not TOKEN or not PROJECT:
        raise SystemExit("CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN are not set.")
    apply = "--apply" in sys.argv[1:]

    pattern = wanted_pattern()
    print(f"{CONFIG} wants: {pattern}")

    everything = listing(f"/projects/{PROJECT}/files")
    named = [f for f in everything if f["name"] == SOURCE_FILE]
    if not named:
        raise SystemExit(f"No '{SOURCE_FILE}' in the project.")

    # The live file is the one carrying the most strings; an orphan is a snapshot of an older
    # upload and can only be behind. Ties keep the most recently updated.
    def freshness(f):
        return (len(listing(f"/projects/{PROJECT}/strings", fileId=f["id"])), f.get("updatedAt", ""))

    ranked = sorted(named, key=freshness, reverse=True)
    source, orphans = ranked[0], ranked[1:]

    print(f"Live source: id={source['id']} {source.get('path')}")
    for f in orphans:
        print(f"Orphan:      id={f['id']} {f.get('path')} (exports alongside the live file)")

    current = (source.get("exportOptions") or {}).get("exportPattern")
    project = call(f"/projects/{PROJECT}")["data"]
    mapping = project.get("languageMapping") or {}

    print(f"Crowdin has:  {current}")
    print(f"languageMapping: {json.dumps(mapping)}")

    changes = []
    if current != pattern:
        changes.append("export pattern")
    if mapping:
        changes.append(f"languageMapping ({len(mapping)} entr(y/ies) to clear)")
    if orphans:
        changes.append(f"{len(orphans)} orphaned source file(s) to delete")
    if not changes:
        print("\nAlready correct - nothing to write.")
        return 0

    print(f"\n{'Writing' if apply else 'Would write'}: {', '.join(changes)}")
    if not apply:
        print("Nothing was written - re-run with --apply.")
        return 0

    if current != pattern:
        call(f"/projects/{PROJECT}/files/{source['id']}", method="PATCH",
             body=[{"op": "replace", "path": "/exportOptions",
                    "value": {"exportPattern": pattern}}])
        print("  export pattern set")
    if mapping:
        call(f"/projects/{PROJECT}", method="PATCH",
             body=[{"op": "replace", "path": "/languageMapping", "value": {}}])
        print("  languageMapping cleared")
    for f in orphans:
        call(f"/projects/{PROJECT}/files/{f['id']}", method="DELETE")
        print(f"  deleted orphan id={f['id']} {f.get('path')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
