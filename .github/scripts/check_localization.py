#!/usr/bin/env python3
"""
Localization guard for both heads.

Four checks, all hard failures, so the base translation file (en.json) stays 1:1 with the
code and Crowdin always has every user-facing string to translate (issue #560):

  A. Every localization KEY referenced in code exists in en.json.
     - C#:   GetString("key"), "key".Localized(), L10n.Get("key")
     - XAML: {l:Localize key}   (Avalonia .axaml and MAUI .xaml alike)
  B. No hard-coded, user-facing string literals in markup (Text / Watermark / ToolTip.Tip /
     Content / Placeholder / Title). Use {l:Localize key} + an en.json entry instead. A small
     ALLOWLIST covers strings that are intentionally not translated: example placeholders
     (a token/URL/CSS sample) and product names.
  C. No hard-coded prose reaching a user-facing sink in C# — a dialog, a status line, an install
     failure message. A literal there must either name an en.json key (several sinks take a key
     and localize internally) or come from a lookup ("key".Localized() / L10n.Get("key")).
     Interpolated strings are read as their literal parts, so $"{L("k")}: {ex}" is fine while
     $"Failed to open: {ex}" is not.
  D. Every translation carries the same {0}/{1} placeholders its English source does. A
     translation that drops one leaves string.Format with nothing to substitute, so the message
     names the wrong app (or the wrong number, or the wrong path) in that language — and one that
     invents an index en.json doesn't have throws FormatException at runtime. This is what a
     Crowdin sync writing an older revision back over a fix looks like (PR #610), and without
     this check it merges green.

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
LOCALIZATION = DESKTOP / "Assets" / "Localization"
EN = LOCALIZATION / "en.json"

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
    "e.g. HarborTV",
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


# C# sinks whose argument a user reads on screen. Calls first, then the status properties both
# heads' view models assign to directly.
CS_SINK_CALL = re.compile(
    r'(?:SetStatus|DisplayAlert|DisplayActionSheet|ShowMessageAsync|ShowErrorAsync'
    r'|ShowConfirmationAsync|PromptForTextAsync|ShowCertificateCountdownAsync|FailureResult)\s*\(')
CS_SINK_ASSIGN = re.compile(r'\b(?:StatusText|Status)\s*=\s*(?=[$@"])')
WORDS = re.compile(r"[A-Za-z]{3,}")

# string.Format placeholders. `{{` is a literal brace and is stripped before matching.
PLACEHOLDER = re.compile(r"\{(\d+)\}")


def read_string(text, i):
    """Reads the C# string literal at text[i] ("…", $"…", @"…", $@"…"). Returns (end, literal),
    where the literal keeps only the text OUTSIDE interpolation holes — a hole is code, and any
    string inside it is a key, not prose."""
    interpolated = verbatim = False
    while text[i] in '$@':
        interpolated |= text[i] == '$'
        verbatim |= text[i] == '@'
        i += 1
    i += 1                                     # the opening quote
    out = []
    while i < len(text):
        c = text[i]
        if c == '\\' and not verbatim:
            if text[i + 1:i + 2] == 'u':
                out.append(chr(int(text[i + 2:i + 6], 16)))
                i += 6
            else:
                out.append({'n': '\n', 't': '\t'}.get(text[i + 1:i + 2], text[i + 1:i + 2]))
                i += 2
            continue
        if c == '"':
            if verbatim and text[i + 1:i + 2] == '"':
                out.append('"')
                i += 2
                continue
            return i + 1, ''.join(out)
        if interpolated and c == '{':
            if text[i + 1:i + 2] == '{':
                out.append('{')
                i += 2
                continue
            depth, i = 1, i + 1
            while i < len(text) and depth:
                if text[i] == '"' or (text[i] in '$@' and text[i + 1:i + 2] in ('"', '$', '@')):
                    i, _ = read_string(text, i)
                    continue
                depth += {'{': 1, '}': -1}.get(text[i], 0)
                i += 1
            out.append('{}')                   # stands in for the interpolated value
            continue
        out.append(c)
        i += 1
    return i, ''.join(out)


def sink_literals(text, start, single_expression=False):
    """Literals handed DIRECTLY to a sink: depth 1 of a call's argument list, or the right-hand
    side of an assignment. A literal followed by .Localized() is a lookup, so it is not prose."""
    found = []
    if single_expression:
        end = text.find(';', start)
        region, depth_ok = text[start:end if end > 0 else start], lambda _: True
    else:
        region, depth_ok = text[start:], None

    i, depth = 0, 0
    while i < len(region):
        c = region[i]
        if not single_expression:
            if c == '(':
                depth += 1
            elif c == ')':
                depth -= 1
                if depth == 0:
                    break
        if c == '"' or (c in '$@' and region[i + 1:i + 2] in ('"', '$', '@')):
            end, literal = read_string(region, i)
            if single_expression or depth == 1:
                if not region[end:end + 13].lstrip().startswith(".Localized()"):
                    found.append(literal)
            i = end
            continue
        i += 1
    return found


def is_prose(literal, keys):
    """Prose = text a user reads. Not a key, an identifier, a format token or punctuation.
    Deliberately catches short button labels too ("OK", "Yes"), since those are exactly the ones
    that get left untranslated; anything that is really a key or a sample is excluded above."""
    if literal in keys or literal in ALLOWLIST:
        return False
    stripped = literal.replace('{}', ' ').strip()
    return bool(re.search(r"[A-Za-z]{2,}", stripped))


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


def listed(indexes):
    return ", ".join("{%s}" % i for i in sorted(indexes, key=int))


def placeholders(text):
    """The set of {N} indexes a string substitutes. A set rather than a list: a translation may
    naturally repeat a placeholder more or fewer times than English does ("{0} … {0}" reads badly
    in some languages), which is fine — dropping or inventing an index is not."""
    return set(PLACEHOLDER.findall(text.replace("{{", "").replace("}}", "")))


def main():
    english = json.loads(EN.read_text(encoding="utf-8"))
    keys = set(english.keys())
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

    # C. no hard-coded prose reaching a user-facing sink in C#
    for f in source_files(".cs"):
        text = f.read_text(encoding="utf-8", errors="ignore")
        for match in CS_SINK_CALL.finditer(text):
            for literal in sink_literals(text, match.end() - 1):
                if is_prose(literal, keys):
                    line = text[:match.start()].count("\n") + 1
                    errors.append(
                        f"[hardcoded-cs] {f}:{line}: {match.group(0).rstrip('( ')}(… \"{literal}\") "
                        f"— pass a key instead (\"key\".Localized() / L10n.Get(\"key\"))")
        for match in CS_SINK_ASSIGN.finditer(text):
            for literal in sink_literals(text, match.end(), single_expression=True):
                if is_prose(literal, keys):
                    line = text[:match.start()].count("\n") + 1
                    errors.append(
                        f"[hardcoded-cs] {f}:{line}: {match.group(0).strip()} \"{literal}\" "
                        f"— pass a key instead (\"key\".Localized() / L10n.Get(\"key\"))")

    # D. every translation substitutes the same placeholders as its English source
    for f in sorted(LOCALIZATION.glob("*.json")):
        if f == EN:
            continue
        translations = json.loads(f.read_text(encoding="utf-8"))
        for k, translated in translations.items():
            source = english.get(k)
            if not isinstance(source, str) or not isinstance(translated, str):
                continue                       # a key en.json no longer has is simply unused
            want, got = placeholders(source), placeholders(translated)
            if want == got:
                continue
            problems = []
            if want - got:
                problems.append("drops " + listed(want - got))
            if got - want:
                problems.append("adds " + listed(got - want))
            errors.append(
                f"[placeholder] {f}: \"{k}\" {' and '.join(problems)} — en.json substitutes "
                f"{listed(want) if want else 'nothing'}")

    if errors:
        print("Localization check FAILED (%d issue(s)):\n" % len(errors) + "\n".join(sorted(errors)))
        return 1
    print("Localization check passed — every key exists in en.json, no hard-coded UI strings "
          "(desktop + mobile), and every translation keeps its placeholders.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
