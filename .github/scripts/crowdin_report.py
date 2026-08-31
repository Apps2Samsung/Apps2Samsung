#!/usr/bin/env python3
"""
Read-only: what Crowdin holds per language, and which repo file each one lands in.

For answering "why isn't this language syncing". A language reaches the repo only if all of
these line up, and the sync log says nothing when one doesn't - it just quietly ships fewer
files than the project has languages (pt-PT and zh-CN have never come down; 27 files for 29
languages, since long before the CLI 5 upgrade):

  - the language is a target language of the project
  - it has translated strings in the beta branch's en.json, since skip_untranslated_strings
    leaves an untranslated language with an empty file, and Crowdin omits an empty file
    from the export archive entirely
  - the path crowdin.yml resolves for it is the file the app actually reads

Nothing here writes. CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN in the environment.
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
FULL_CODE_FILES = {"pt-PT", "zh-CN"}          # kept in step with crowdin.yml's languages_mapping

TOKEN = os.environ.get("CROWDIN_PERSONAL_TOKEN", "")
PROJECT = os.environ.get("CROWDIN_PROJECT_ID", "")


def call(path, **params):
    url = f"{API}{path}" + ("?" + urllib.parse.urlencode(params) if params else "")
    request = urllib.request.Request(
        url, headers={"Authorization": f"Bearer {TOKEN}"})
    try:
        with urllib.request.urlopen(request) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as e:
        raise SystemExit(f"GET {url} -> HTTP {e.code}: {e.read().decode(errors='replace')[:400]}")


def listing(path, **params):
    out, offset = [], 0
    while True:
        page = call(path, limit=500, offset=offset, **params)["data"]
        out += [row["data"] for row in page]
        if len(page) < 500:
            return out
        offset += 500


def main():
    if not TOKEN or not PROJECT:
        raise SystemExit("CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN are not set.")

    project = call(f"/projects/{PROJECT}")["data"]
    branches = [b for b in listing(f"/projects/{PROJECT}/branches") if b["name"] == BRANCH]
    if not branches:
        raise SystemExit(f"No '{BRANCH}' branch in the project.")
    files = [f for f in listing(f"/projects/{PROJECT}/files", branchId=branches[0]["id"])
             if f["name"] == SOURCE_FILE]
    if not files:
        raise SystemExit(f"No '{SOURCE_FILE}' under '{BRANCH}'.")
    file_id = files[0]["id"]

    progress = {p["languageId"]: p
                for p in listing(f"/projects/{PROJECT}/files/{file_id}/languages/progress")}
    languages = {l["id"]: l for l in project["targetLanguages"]}

    print(f"{project['name']} #{PROJECT} - {BRANCH}/{SOURCE_FILE} (file {file_id}), "
          f"{len(languages)} target languages\n")
    print(f"{'language':>8}  {'repo file':<14} {'translated':>12}  {'approved':>8}")
    print("-" * 50)

    silent = []
    for code in sorted(languages):
        p = progress.get(code, {})
        phrases = p.get("phrases", {})
        translated, total = phrases.get("translated", 0), phrases.get("total", 0)
        name = f"{code if code in FULL_CODE_FILES else languages[code]['twoLettersCode']}.json"
        path = LOCALIZATION / name
        note = "" if path.exists() else "  <- no such file in the repo"
        if not translated:
            note += "  <- empty export, Crowdin omits the file"
            silent.append(code)
        print(f"{code:>8}  {name:<14} {translated:>6}/{total:<5}  "
              f"{p.get('approvalProgress', 0):>7}%{note}")

    shipped = sorted(f.name for f in LOCALIZATION.glob("*.json") if f.name != SOURCE_FILE)
    print(f"\n{len(shipped)} language files in the repo; {len(languages)} languages in Crowdin.")
    if silent:
        print(f"Never exported (nothing translated): {', '.join(silent)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
