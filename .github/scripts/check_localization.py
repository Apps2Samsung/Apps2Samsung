#!/usr/bin/env python3
"""
Localization guard for the desktop app.

Two checks, both hard failures, so the base translation file (en.json) stays 1:1 with the
code and Crowdin always has every user-facing string to translate (issue #560):

  A. Every localization KEY referenced in code exists in en.json.
     - C#:   GetString("key")  and  "key".Localized()
     - XAML: {l:Localize key}
  B. No hard-coded, user-facing string literals in XAML (Text / Watermark / ToolTip.Tip /
     Content). Use {l:Localize key} + an en.json entry instead. A small ALLOWLIST covers
     example placeholders that are intentionally not translated (a token/URL format sample).

Run from the repo root:  python3 .github/scripts/check_localization.py
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path("Jellyfin2Samsung-CrossOS")
EN = ROOT / "Assets" / "Localization" / "en.json"

# Literal placeholder samples that are intentionally NOT translated (example token / URL / CSS).
ALLOWLIST = {
    "ghp_xxxxxxxxxxxxxxxxxxxx",
    "https://example.com:8096/jellyfin",
    "/jellyfin",
    "@import url('https://cdn.jsdelivr.net/...');",
}

# Strips XML character/entity references so emoji glyphs (e.g. &#x1F319;) aren't seen as "letters".
ENTITY = re.compile(r"&#x?[0-9A-Fa-f]+;|&[a-zA-Z]+;")

CS_GETSTRING = re.compile(r'GetString\(\s*"([^"]+)"')
CS_LOCALIZED = re.compile(r'"([^"]+)"\s*\.\s*Localized\(\)')
XAML_LOCALIZE = re.compile(r'\{\s*l:Localize\s+([A-Za-z0-9_]+)')
XAML_LITERAL = re.compile(r'\b(Text|Watermark|ToolTip\.Tip|Content)\s*=\s*"([^"]+)"')


def source_files(suffix):
    for f in ROOT.rglob(f"*{suffix}"):
        p = f.as_posix()
        if "/bin/" in p or "/obj/" in p:
            continue
        yield f


def main():
    keys = set(json.loads(EN.read_text(encoding="utf-8")).keys())
    errors = []

    # A. referenced keys must exist
    for f in source_files(".cs"):
        text = f.read_text(encoding="utf-8", errors="ignore")
        for k in set(CS_GETSTRING.findall(text)) | set(CS_LOCALIZED.findall(text)):
            if k not in keys:
                errors.append(f"[missing-key] {f}: \"{k}\" is used but not in en.json")
    for f in source_files(".axaml"):
        text = f.read_text(encoding="utf-8", errors="ignore")
        for k in set(XAML_LOCALIZE.findall(text)):
            if k not in keys:
                errors.append(f"[missing-key] {f}: {{l:Localize {k}}} is used but not in en.json")

    # B. no hard-coded user-facing literals in XAML
    for f in source_files(".axaml"):
        text = f.read_text(encoding="utf-8", errors="ignore")
        for attr, val in XAML_LITERAL.findall(text):
            if val.startswith("{"):
                continue                       # binding / markup extension
            if val in ALLOWLIST:
                continue
            if not re.search(r"[A-Za-z]", ENTITY.sub("", val)):
                continue                       # emoji entities / numbers / symbols only
            errors.append(
                f"[hardcoded] {f}: {attr}=\"{val}\" — use {{l:Localize key}} + an en.json entry"
            )

    if errors:
        print("Localization check FAILED (%d issue(s)):\n" % len(errors) + "\n".join(sorted(errors)))
        return 1
    print("Localization check passed — every key exists in en.json and no hard-coded UI strings.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
