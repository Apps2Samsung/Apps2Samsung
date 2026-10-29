#!/usr/bin/env bash
# Version guard for pull requests (run by pr-build.yml, Core + desktop job).
#
# The version a release ships is copied by hand into four places, and the branch is expected to
# move past the last stable before anything else lands on beta. Both have gone wrong quietly:
#
#   * beta stayed at the version master had just released (3.1.0 stable out, beta still 3.1.0).
#     beta-prerelease.yml moves the `v3.1.0-beta` tag with `git tag -f`, so the next merge would
#     have republished a beta *under the tag of a shipped stable*, and stable-release.yml would
#     later have refused the same version ("release already exists").
#   * one of the four version fields was bumped and the others not, so the Android build, the
#     macOS bundle and the desktop footer disagreed about what version they were.
#
# Checks, all hard failures:
#   1. AppSettings.AppVersion is a clean vX.Y.Z (no -beta: CI stamps that itself, see
#      stamp-beta-version.py, and stable-release.yml refuses a committed marker).
#   2. CFBundleVersion (desktop csproj), Info.plist's CFBundleVersion and the Android
#      ApplicationDisplayVersion all equal that X.Y.Z.
#   3. The version is higher than the highest stable tag on origin (vX.Y.Z without a suffix),
#      so a merge to beta can never re-tag a shipped stable's beta.
#   4. stamp-beta-version.py still finds the version line (dry run on a copy), so a rename of the
#      field or the file fails here and not in the beta release.
#
# Usage: .github/scripts/check-version.sh            # from the repo root, needs `origin`
#        .github/scripts/check-version.sh <stable>   # compare against this tag instead of origin
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
SETTINGS="Apps2Samsung.Desktop/Helpers/AppSettings.cs"
DESKTOP_CSPROJ="Apps2Samsung.Desktop/Apps2Samsung.csproj"
PLIST="Apps2Samsung.Desktop/Info.plist"
MOBILE_CSPROJ="Apps2Samsung.Mobile/Apps2Samsung.Mobile.csproj"
STAMP=".github/scripts/stamp-beta-version.py"

fail() { echo "::error::$*"; exit 1; }
cd "$ROOT"

# 1. AppVersion: the one the release workflows read.
LINE=$(grep -E 'AppVersion\s*\{\s*get;\s*set;\s*\}\s*=' "$SETTINGS" || true)
[ -n "$LINE" ] || fail "$SETTINGS: no AppVersion line"
QUOTED=$(echo "$LINE" | grep -oE '"[^"]*"' | head -n 1 | tr -d '"')
VERSION=$(echo "$QUOTED" | grep -oE '^v[0-9]+(\.[0-9]+){1,3}$' || true)
[ -n "$VERSION" ] || fail "$SETTINGS: AppVersion is \"$QUOTED\", expected a clean vX.Y.Z (CI adds -beta itself)"
PLAIN=${VERSION#v}
echo "AppVersion: $VERSION"

# 2. The other three fields carry the same number.
BUNDLE=$(grep -oE '<CFBundleVersion>[^<]*</CFBundleVersion>' "$DESKTOP_CSPROJ" | sed -E 's/<[^>]+>//g' || true)
PLIST_VERSION=$(grep -A1 '<key>CFBundleVersion</key>' "$PLIST" | grep -oE '<string>[^<]*</string>' | sed -E 's/<[^>]+>//g' || true)
DISPLAY=$(grep -oE '<ApplicationDisplayVersion>[^<]*</ApplicationDisplayVersion>' "$MOBILE_CSPROJ" | sed -E 's/<[^>]+>//g' || true)
echo "CFBundleVersion (csproj): ${BUNDLE:-missing}"
echo "CFBundleVersion (Info.plist): ${PLIST_VERSION:-missing}"
echo "ApplicationDisplayVersion: ${DISPLAY:-missing}"
[ "$BUNDLE" = "$PLAIN" ] || fail "$DESKTOP_CSPROJ: CFBundleVersion is '${BUNDLE:-missing}', AppVersion is $PLAIN"
[ "$PLIST_VERSION" = "$PLAIN" ] || fail "$PLIST: CFBundleVersion is '${PLIST_VERSION:-missing}', AppVersion is $PLAIN"
[ "$DISPLAY" = "$PLAIN" ] || fail "$MOBILE_CSPROJ: ApplicationDisplayVersion is '${DISPLAY:-missing}', AppVersion is $PLAIN"

# 3. Past the last stable. Tags are read off origin, so this needs no tag fetch in the checkout.
if [ $# -ge 1 ]; then
  STABLE=$1
else
  STABLE=$(git ls-remote --tags --refs origin 'v*' | awk '{print $2}' | sed 's|refs/tags/||' \
    | grep -E '^v[0-9]+(\.[0-9]+){1,3}$' | sort -V | tail -n 1 || true)
fi
if [ -z "$STABLE" ]; then
  echo "No stable tag found on origin; skipping the comparison"
else
  echo "Latest stable: $STABLE"
  HIGHEST=$(printf '%s\n%s\n' "$STABLE" "$VERSION" | sort -V | tail -n 1)
  if [ "$VERSION" = "$STABLE" ]; then
    fail "AppVersion $VERSION is already released as stable. Bump the version (all four fields) before merging to beta."
  elif [ "$HIGHEST" != "$VERSION" ]; then
    fail "AppVersion $VERSION is lower than the latest stable $STABLE."
  fi
fi

# 4. The beta stamp still finds its line: dry run on a copy, the working tree stays untouched.
DRY=$(mktemp -d)
trap 'rm -rf "$DRY"' EXIT
mkdir -p "$DRY/.github/scripts" "$DRY/$(dirname "$SETTINGS")"
cp "$STAMP" "$DRY/.github/scripts/"
cp "$SETTINGS" "$DRY/$SETTINGS"
python3 "$DRY/$STAMP" >/dev/null || fail "$STAMP no longer finds the version line in $SETTINGS"
grep -qF "\"$VERSION-beta\"" "$DRY/$SETTINGS" || fail "$STAMP ran but did not stamp $VERSION-beta"
echo "Beta stamp dry run: $VERSION-beta"

echo "Version check passed: $VERSION, four fields agree, past stable ${STABLE:-n/a}"
