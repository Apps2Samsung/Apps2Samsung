#!/usr/bin/env python3
"""
Localization guard for both heads.

Two checks, both hard failures, so the base translation file (en.json) stays 1:1 with the
code and Crowdin always has every user-facing string to translate (issue #560):

  A. Every localization KEY referenced in code exists in en.json.
     - C#:   GetString("key"), "key".Localized(), L10n.Get("key")
     - XAML: {l:Localize key}   (Avalonia .axaml and MAUI .xaml alike)
  B. No hard-coded, user-facing string literals in markup (Text / Watermark / ToolTip.Tip /
     Content / Placeholder / Title). Use {l:Localize key} + an en.json entry instead. A small
     ALLOWLIST covers strings that are intentionally not translated: example placeholders
     (a token/URL/CSS sample) and product names.

Both heads are checked: the desktop head (Avalonia) and the mobile head (MAUI), which shares
the same en.json now that the string catalog lives in Core.

Run from the repo root:  python3 .github/scripts/check_localization.py
"""
import json
import re
import sys
from pathlib import Path

DESKTOP = Path("Jellyfin2Samsung-CrossOS")
MOBILE = Path("Apps2Samsung.Mobile")
EN = DESKTOP / "Assets" / "Localization" / "en.json"

# Strings that are intentionally NOT translated: example token / URL / CSS samples, and the
# product names themselves.
ALLOWLIST = {
    "ghp_xxxxxxxxxxxxxxxxxxxx",
    "ghp_xxxxxxxxxxxxxxxxx",
    "https://example.com:8096/jellyfin",
    "http://192.168.1.10:8096",
    "/jellyfin",
    "@import url('https://cdn.jsdelivr.net/...');",
    "/* your CSS here */",
    "Apps2Samsung",
    "Jellyfin",
}

# Strips XML character/entity references so emoji glyphs (e.g. &#x1F319;) aren't seen as "letters".
ENTITY = re.compile(r"&#x?[0-9A-Fa-f]+;|&[a-zA-Z]+;")

CS_GETSTRING = re.compile(r'GetString\(\s*"([^"]+)"')
CS_LOCALIZED = re.compile(r'"([^"]+)"\s*\.\s*Localized\(\)')
CS_L10N = re.compile(r'L10n\.Get\(\s*"([^"]+)"')
# Both forms the extension accepts: positional ({l:Localize key}) and named, which is how a
# label that keeps an icon passes a format ({l:Localize Key=key, Format='\U0001F507 {0}'}).
XAML_LOCALIZE = re.compile(r'\{\s*l:Localize\s+(?:Key\s*=\s*)?([A-Za-z0-9_]+)')
XAML_LITERAL = re.compile(
    r'\b(Text|Watermark|ToolTip\.Tip|Content|Placeholder|Title)\s*=\s*"([^"]+)"')


def source_files(suffix, roots=(DESKTOP, MOBILE)):
    for root in roots:
        for f in root.rglob(f"*{suffix}"):
            p = f.as_posix()
            if "/bin/" in p or "/obj/" in p:
                continue
            yield f


def markup_files():
    """Avalonia markup in the desktop head, MAUI markup in the mobile head."""
    yield from source_files(".axaml", roots=(DESKTOP,))
    yield from source_files(".xaml", roots=(MOBILE,))


def main():
    keys = set(json.loads(EN.read_text(encoding="utf-8")).keys())
    errors = []

    # A. referenced keys must exist. Each head has its own accessor, and they are checked
    # separately on purpose: the mobile head has an unrelated local GetString(name) helper for
    # reading a settings backup, which the desktop pattern would otherwise read as string keys.
    for f in source_files(".cs", roots=(DESKTOP,)):
        text = f.read_text(encoding="utf-8", errors="ignore")
        for k in set(CS_GETSTRING.findall(text)) | set(CS_LOCALIZED.findall(text)):
            if k not in keys:
                errors.append(f"[missing-key] {f}: \"{k}\" is used but not in en.json")
    for f in source_files(".cs", roots=(MOBILE,)):
        text = f.read_text(encoding="utf-8", errors="ignore")
        for k in set(CS_L10N.findall(text)):
            if k not in keys:
                errors.append(f"[missing-key] {f}: \"{k}\" is used but not in en.json")
    for f in markup_files():
        text = f.read_text(encoding="utf-8", errors="ignore")
        for k in set(XAML_LOCALIZE.findall(text)):
            if k not in keys:
                errors.append(f"[missing-key] {f}: {{l:Localize {k}}} is used but not in en.json")

    # B. no hard-coded user-facing literals in markup
    for f in markup_files():
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
    print("Localization check passed — every key exists in en.json and no hard-coded UI strings "
          "(desktop + mobile).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
