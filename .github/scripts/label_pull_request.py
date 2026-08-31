#!/usr/bin/env python3
"""
Puts a changelog category on a pull request, so the release notes sort themselves.

.github/release.yml groups the generated notes by label. Nothing was labelled, so every release
landed as one flat "Other changes" list with the Remote page and a CI tweak side by side - and the
labels it groups by (ci, chore, translations, l10n, feature, fix) did not even exist in the repo
until they were created by hand, so nothing could ever have reached those categories.

This labels only what it can tell for certain, and says nothing when it can't:

  1. A pull request that already carries a category label is left alone. A person decided; that
     beats a regex.
  2. What the diff touches, which is the strongest signal - only language files is a translation,
     only .github/ is CI, only prose is documentation, whatever the title claims. (The tooling for
     the Crowdin sync was titled "i18n:" and belonged under CI, which is exactly this case.)
  3. Failing that, the title prefix this repo already uses: docs:, CI:, deps:, i18n:, fix:, feat:.
  4. Failing that, the branch prefix: fix/, feature/, ci/, docs/.

Anything else is left unlabelled on purpose. "Remote: use the phone as a TV remote" is a feature
and "Mobile: pick files through SAF so cloud sources stop throwing" is a fix, and nothing in
either sentence says so - guessing would put the wrong thing in front of a reader, which is worse
than the one click it costs to say.
"""
import json
import os
import re
import sys
import urllib.request

API = "https://api.github.com"

# Every label .github/release.yml groups by. One of these already present means hands off.
CATEGORIES = {"feature", "enhancement", "bug", "fix", "translations", "l10n",
              "chore", "ci", "dependencies", "documentation"}

LOCALIZATION = "Jellyfin2Samsung-CrossOS/Assets/Localization/"

TITLE_RULES = [
    (r"^docs\b", "documentation"),
    (r"^(ci|fdroid)\b", "ci"),
    (r"^deps\b", "dependencies"),
    (r"^i18n\b", "translations"),
    (r"^fix\b", "fix"),
    (r"^feat(ure)?\b", "feature"),
]

BRANCH_RULES = [
    ("fix/", "fix"),
    ("feature/", "feature"),
    ("feat/", "feature"),
    ("ci/", "ci"),
    ("docs/", "documentation"),
]


def call(path, method="GET", body=None):
    request = urllib.request.Request(
        f"{API}{path}", method=method,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Authorization": f"Bearer {os.environ['GITHUB_TOKEN']}",
                 "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request) as response:
        payload = response.read()
    return json.loads(payload) if payload else {}


def changed_files(repo, number):
    files, page = [], 1
    while page <= 10:                          # 300 files is plenty to classify a pull request
        batch = call(f"/repos/{repo}/pulls/{number}/files?per_page=100&page={page}")
        files += [f["filename"] for f in batch]
        if len(batch) < 100:
            break
        page += 1
    return files


def from_paths(files):
    if not files:
        return None
    if all(f.startswith(LOCALIZATION) for f in files):
        return "translations"
    if all(f.startswith(".github/") for f in files):
        return "ci"
    if all(f.endswith(".md") or f.startswith("docs/") for f in files):
        return "documentation"
    return None


def from_title(title):
    lowered = title.strip().lower()
    for pattern, label in TITLE_RULES:
        if re.match(pattern, lowered):
            return label
    return None


def from_branch(branch):
    for prefix, label in BRANCH_RULES:
        if branch.lower().startswith(prefix):
            return label
    return None


def main():
    with open(os.environ["GITHUB_EVENT_PATH"], encoding="utf-8") as handle:
        event = json.load(handle)

    pr = event["pull_request"]
    repo = event["repository"]["full_name"]
    number = pr["number"]

    existing = {label["name"] for label in pr.get("labels", [])}
    if existing & CATEGORIES:
        print(f"#{number} already has {sorted(existing & CATEGORIES)} - leaving it alone.")
        return 0

    if pr.get("user", {}).get("login", "").startswith("dependabot"):
        label = "dependencies"
        why = "dependabot"
    else:
        files = changed_files(repo, number)
        label = from_paths(files)
        why = f"the diff ({len(files)} file(s))"
        if not label:
            label, why = from_title(pr["title"]), "the title"
        if not label:
            label, why = from_branch(pr["head"]["ref"]), "the branch name"

    if not label:
        print(f"#{number} {pr['title']!r}: nothing conclusive - leaving it for a person.")
        return 0

    call(f"/repos/{repo}/issues/{number}/labels", method="POST", body={"labels": [label]})
    print(f"#{number} labelled '{label}' from {why}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
