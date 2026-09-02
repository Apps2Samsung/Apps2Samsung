#!/usr/bin/env python3
"""
Corrects ONE localization key in Crowdin, in every language, from what the repo already holds.

Why this exists: crowdin-sync.yml uploads sources and downloads translations, so a string fixed
by hand in the repo never reaches Crowdin. Uploading translations doesn't necessarily fix it
either - Crowdin exports an APPROVED translation over a newer unapproved one, so an import lands
underneath the very string it was meant to replace and the next sync hands the old text straight
back. That is what PR #610 was: alreadyInstalled reverted in 27 languages, months after #603
fixed it, with every required check green.

Blunt instruments exist (auto_approve_imported) but they approve every string in every file,
including the machine translations that are explicitly not native-speaker reviewed. This touches
one key: for each language it removes the approval and the translations Crowdin holds, uploads
the text from that language's repo file, and approves that instead - so the sync stops handing
back the old revision, and nothing else changes its review state.

  CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN in the environment, the key as argv[1].
  Pass --apply to write; without it the script only reports what it would do.
"""
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

API = "https://api.crowdin.com/api/v2"
LOCALIZATION = Path("Jellyfin2Samsung-CrossOS/Assets/Localization")
SOURCE_FILE = "en.json"
BRANCH = "beta"

# crowdin.yml names every language file by its full locale (%locale%), so the repo file for a
# language is simply <locale>.json - nl-NL.json, pt-BR.json, zh-TW.json. The short two-letter
# naming this used to mirror is what collided pt-BR with pt-PT and zh-CN with zh-TW.

TOKEN = os.environ.get("CROWDIN_PERSONAL_TOKEN", "")
PROJECT = os.environ.get("CROWDIN_PROJECT_ID", "")


def call(method, path, body=None, **params):
    url = f"{API}{path}"
    if params:
        url += "?" + urllib.parse.urlencode(params)
    request = urllib.request.Request(
        url, method=method,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Authorization": f"Bearer {TOKEN}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request) as response:
            payload = response.read()
        return json.loads(payload) if payload else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode(errors="replace")[:400]
        raise SystemExit(f"{method} {url} -> HTTP {e.code}: {detail}")


def listing(path, **params):
    """Every list endpoint here fits well inside one page of 500, but paginate anyway."""
    out, offset = [], 0
    while True:
        page = call("GET", path, limit=500, offset=offset, **params)["data"]
        out += [row["data"] for row in page]
        if len(page) < 500:
            return out
        offset += 500


def language_file(language):
    """The repo file a Crowdin language maps to, or None if the repo doesn't ship it."""
    code = language.get("locale") or language["id"]
    path = LOCALIZATION / f"{code}.json"
    return path if path.exists() else None


def source_file(project_files):
    """The one file crowdin.yml describes, and a hard error when the project holds more than one
    candidate.

    Crowdin accumulates orphans: a source uploaded under a different path, or before the branch
    existed, stays in the project and keeps exporting. The project carried three files all named
    en.json - the live one plus two left from earlier layouts - and every build contained all of
    them, which is how a 29-language export came back with 56 entries. Picking whichever the API
    listed first is exactly the bug that hid pt-PT and zh-CN, one level up, so this refuses to
    guess: delete the orphans (crowdin-config does it) rather than let a script choose.
    """
    candidates = [f for f in project_files if f["name"] == SOURCE_FILE]
    if not candidates:
        raise SystemExit(f"No '{SOURCE_FILE}' in the project.")
    if len(candidates) > 1:
        listed = "\n".join(
            f"    id={f['id']} branch={f.get('branchId')} path={f.get('path')}" for f in candidates)
        raise SystemExit(
            f"{len(candidates)} files named '{SOURCE_FILE}' - refusing to guess which one is "
            f"live:\n{listed}\n  Run the 'Crowdin config' workflow to remove the orphans.")
    return candidates[0]


def main():
    if not TOKEN or not PROJECT:
        raise SystemExit("CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN are not set.")
    args = [a for a in sys.argv[1:] if a != "--apply"]
    apply = "--apply" in sys.argv[1:]
    if len(args) != 1:
        raise SystemExit("usage: crowdin_fix_string.py <en.json key> [--apply]")
    key = args[0]

    english = json.loads((LOCALIZATION / SOURCE_FILE).read_text(encoding="utf-8"))
    if key not in english:
        raise SystemExit(f'"{key}" is not a key in {SOURCE_FILE}.')

    project = call("GET", f"/projects/{PROJECT}")["data"]
    print(f"Project: {project['name']} (#{PROJECT})")

    source = source_file(listing(f"/projects/{PROJECT}/files"))
    print(f"Source of truth: id={source['id']} {source.get('path')}")

    strings = [s for s in listing(f"/projects/{PROJECT}/strings", fileId=source["id"])
               if s["identifier"] == key]
    if not strings:
        raise SystemExit(f'"{key}" is not in Crowdin - upload the sources first.')
    string_id = strings[0]["id"]
    print(f'"{key}" is string {string_id}; English source:\n  {strings[0]["text"]}\n')

    changed = skipped = 0
    for language in sorted(project["targetLanguages"], key=lambda l: l["id"]):
        path = language_file(language)
        if path is None:
            print(f"{language['id']:>6}  - no file in the repo, left alone")
            continue
        wanted = json.loads(path.read_text(encoding="utf-8")).get(key)
        if wanted is None:
            print(f"{language['id']:>6}  - {path.name} has no {key}, left alone")
            continue

        existing = listing(f"/projects/{PROJECT}/translations",
                           stringId=string_id, languageId=language["id"])
        approvals = listing(f"/projects/{PROJECT}/approvals",
                            stringId=string_id, languageId=language["id"])
        # An approval is not required for Crowdin to export a string - export_only_approved is
        # off, so the single stored translation is the one that ships. Text match is the test;
        # demanding an approval too would report every language as needing a rewrite it doesn't.
        if len(existing) == 1 and existing[0]["text"] == wanted:
            skipped += 1
            state = "approved" if approvals else "unapproved, which still exports"
            print(f"{language['id']:>6}  ✓ already matches the repo ({state})")
            continue

        current = existing[0]["text"] if existing else "(none)"
        print(f"{language['id']:>6}  {current[:60]!r}\n        -> {wanted[:60]!r}"
              f"  [{len(existing)} translation(s), {len(approvals)} approval(s)]")
        changed += 1
        if not apply:
            continue

        for approval in approvals:
            call("DELETE", f"/projects/{PROJECT}/approvals/{approval['id']}")
        for translation in existing:
            call("DELETE", f"/projects/{PROJECT}/translations/{translation['id']}")
        added = call("POST", f"/projects/{PROJECT}/translations",
                     {"stringId": string_id, "languageId": language["id"], "text": wanted})["data"]
        call("POST", f"/projects/{PROJECT}/approvals", {"translationId": added["id"]})

    verb = "Corrected" if apply else "Would correct"
    print(f"\n{verb} {changed} language(s); {skipped} already matched the repo.")
    if not apply:
        print("Nothing was written - re-run with --apply.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
