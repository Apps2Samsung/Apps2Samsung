#!/usr/bin/env python3
"""
Approves the translations Crowdin holds that carry no approval yet.

Crowdin has no "approve them as they arrive" setting. Its auto-approve options only reach two
things: the strings auto-translation fills in (Settings -> Auto-Translate, "Approve added
translations") and the ones an upload writes (--auto-approve-imported). A translation a person
typed in the editor is never approved by anything but another click, which is why the approved
count sits far behind the translated one - Czech was 100% translated and 14% approved.

Nothing in the repo depends on an approval. crowdin.yml sets skip_untranslated_strings and the
sync never asks for an approved-only export, so a translated string ships whether it carries one
or not. The approval is a signal inside Crowdin: an approved string drops out of the proofreading
filters and reads as settled to the next translator who opens the file.

Which is also the cost of running this over everything at once - the tick stops meaning "a native
speaker read this", because the machine translations get it too, and the only record of which
languages someone actually went through is gone. Hence --languages: approve the ones that have
been reviewed and leave the rest alone.

A translation whose text is the English source is never approved. That is what an untranslated key
looks like once it has been uploaded - a language file that carried English for the keys nobody had
reached yet - and approving it is how ~150 strings per language ended up "approved" in English, which
the editor then defends: a translator's real translation sits unapproved underneath it, and entering
it again only gets "Duplicate translation. Please vote or approve the original." The few strings that
legitimately read the same (OK, Debug, brand names) stay unapproved; nothing downstream needs the tick.

One more consequence, the same one crowdin_fix_string.py exists for: Crowdin exports an approved
translation over a newer unapproved one. Approving everything means a string corrected by hand in
the repo comes straight back in the next sync until that script is run for it.

  CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN in the environment.
  --languages cs,nl-NL,...  only these languages; without it, every target language.
  --apply writes the approvals; without it the script reports what it would approve.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.crowdin.com/api/v2"
SOURCE_FILE = "en.json"
PAGE = 500

TOKEN = os.environ.get("CROWDIN_PERSONAL_TOKEN", "")
PROJECT = os.environ.get("CROWDIN_PROJECT_ID", "")


def call(method, path, body=None, **params):
    url = f"{API}{path}"
    if params:
        url += "?" + urllib.parse.urlencode(params)
    data = json.dumps(body).encode() if body is not None else None
    # A first run is one POST per unapproved translation - 580 strings across 29 languages - so it
    # will meet Crowdin's rate limit. A 429 there is the expected answer, not a failure: back off
    # and ask again rather than losing the run halfway through.
    for attempt in range(6):
        request = urllib.request.Request(
            url, method=method, data=data,
            headers={"Authorization": f"Bearer {TOKEN}", "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request) as response:
                payload = response.read()
            return json.loads(payload) if payload else {}
        except urllib.error.HTTPError as e:
            if e.code in (429, 500, 502, 503, 504) and attempt < 5:
                time.sleep(2 ** attempt)
                continue
            detail = e.read().decode(errors="replace")[:400]
            raise SystemExit(f"{method} {url} -> HTTP {e.code}: {detail}")


def listing(path, **params):
    out, offset = [], 0
    while True:
        page = call("GET", path, limit=PAGE, offset=offset, **params)["data"]
        out += [row["data"] for row in page]
        if len(page) < PAGE:
            return out
        offset += PAGE


def source_file(project_files):
    """The one file crowdin.yml describes, and a hard error when the project holds more than one
    candidate.

    Crowdin accumulates orphans: a source uploaded under a different path, or before the branch
    existed, stays in the project and keeps exporting. The project carried three files all named
    en.json - the live one plus two left from earlier layouts. Approving against the wrong one
    would approve strings nothing ships and report the live file as already done, so this refuses
    to guess: delete the orphans (crowdin-config does it) rather than let a script choose.
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


def translation_ids(row):
    """The ids a language-translations row stands for.

    A plain row is one translation and carries translationId itself; an ICU or plural row carries
    one per form under `plurals`. en.json is flat key/value so only the first shape occurs today,
    but reading both costs two lines and is better than silently skipping a string if a plural
    ever lands in the file.
    """
    if "translationId" in row:
        return [row["translationId"]]
    return [form["translationId"] for form in row.get("plurals", []) if "translationId" in form]


def selected_languages(project, wanted):
    """The target languages to walk, named by Crowdin id (cs) or by locale (cs-CZ) - the repo's
    files are named by locale, so that is the code that comes to mind."""
    languages = sorted(project["targetLanguages"], key=lambda l: l["id"])
    if not wanted:
        return languages
    by_code = {}
    for language in languages:
        by_code[language["id"].lower()] = language
        by_code[(language.get("locale") or language["id"]).lower()] = language
    chosen, unknown = [], []
    for code in wanted:
        language = by_code.get(code.lower())
        if language is None:
            unknown.append(code)
        elif language not in chosen:
            chosen.append(language)
    if unknown:
        raise SystemExit(
            f"Not a target language of this project: {', '.join(unknown)}\n  Known: "
            + ", ".join(f"{l['id']}/{l.get('locale')}" for l in languages))
    return chosen


def main():
    if not TOKEN or not PROJECT:
        raise SystemExit("CROWDIN_PROJECT_ID / CROWDIN_PERSONAL_TOKEN are not set.")
    argv = sys.argv[1:]
    apply = "--apply" in argv
    wanted = []
    if "--languages" in argv:
        index = argv.index("--languages") + 1
        if index >= len(argv):
            raise SystemExit("--languages needs a comma-separated list, e.g. --languages cs,nl-NL")
        wanted = [code.strip() for code in argv[index].split(",") if code.strip()]

    project = call("GET", f"/projects/{PROJECT}")["data"]
    print(f"Project: {project['name']} (#{PROJECT})")

    source = source_file(listing(f"/projects/{PROJECT}/files"))
    print(f"Source of truth: id={source['id']} {source.get('path')}\n")

    languages = selected_languages(project, wanted)
    # English text per string, to keep an untranslated copy of it from being approved.
    source_text = {row["id"]: row.get("text")
                   for row in listing(f"/projects/{PROJECT}/strings", fileId=source["id"])}
    approved_total = pending_total = english_total = 0
    for language in languages:
        code = language["id"]
        # The top translation per string, which is the one that ships and the one an approval
        # belongs on. Older revisions of a string keep their own ids and are left alone.
        translations = listing(f"/projects/{PROJECT}/languages/{code}/translations",
                               fileId=source["id"])
        # Approvals can sit on a translation that is no longer the top one - somebody approved it
        # and then a newer translation was added. Comparing by translation id rather than by
        # string is what makes that case come out right: the current text gets approved too.
        approved = {approval["translationId"]
                    for approval in listing(f"/projects/{PROJECT}/approvals",
                                            fileId=source["id"], languageId=code)}
        english = {tid for row in translations
                   if isinstance(row.get("text"), str) and row["text"] == source_text.get(row.get("stringId"))
                   for tid in translation_ids(row)}
        pending = [tid for row in translations for tid in translation_ids(row)
                   if tid not in approved and tid not in english]
        skipped = sum(1 for tid in english if tid not in approved)
        english_total += skipped
        pending_total += len(pending)
        held = sum(len(translation_ids(row)) for row in translations)
        if not held:
            print(f"{code:>6}  nothing translated yet")
            continue
        if skipped:
            print(f"{code:>6}  {skipped} translation(s) identical to the English source - not approved")
        if not pending:
            print(f"{code:>6}  nothing else to approve ({held} translation(s))")
            continue
        print(f"{code:>6}  {len(pending)} of {held} translation(s) unapproved"
              f"{'' if apply else ' - not written'}")
        if not apply:
            continue
        for translation_id in pending:
            call("POST", f"/projects/{PROJECT}/approvals", {"translationId": translation_id})
            approved_total += 1

    verb = "Approved" if apply else "Would approve"
    print(f"\n{verb} {approved_total if apply else pending_total} translation(s) across "
          f"{len(languages)} language(s).")
    if english_total:
        print(f"Left {english_total} translation(s) that only repeat the English source unapproved.")
    if not apply:
        print("Nothing was written - re-run with --apply.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
