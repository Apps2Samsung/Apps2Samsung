#!/usr/bin/env bash
# Renders the AUR PKGBUILD for one release from the template in .github/aur/PKGBUILD.
#
#   aur-pkgbuild.sh <version> <sources-dir> <out-dir> [current-.SRCINFO]
#
#   version         2.7.9 — no leading v, no -beta (the AUR package tracks stable only)
#   sources-dir     holds the four files the PKGBUILD downloads, under their PKGBUILD-local names:
#                     apps2samsung-<version>-LICENSE
#                     apps2samsung-<version>.svg
#                     Apps2Samsung-v<version>-linux-x64.tar.gz
#                     Apps2Samsung-v<version>-linux-arm64.tar.gz
#                   The checksums come from these, so hand it the files users will actually download
#                   (the release assets), not a local rebuild of them.
#   out-dir         receives PKGBUILD
#   current-.SRCINFO  optional: the .SRCINFO currently on the AUR. Decides pkgrel — same version with
#                   different checksums (a release whose assets were rebuilt) bumps pkgrel; a new
#                   version resets it to 1; identical content means there is nothing to publish.
#
# Prints changed=<true|false> and pkgrel=<n>, and writes the same to $GITHUB_OUTPUT when set.
#
# Shared by the Stable Release workflow (renders and publishes) and the PR build (renders from the
# freshly built packages and runs makepkg on the result), so a template mistake shows up on the PR
# rather than on release day.
set -euo pipefail

VERSION="${1:?version, e.g. 2.7.9}"
SOURCES_DIR="$(realpath "${2:?sources dir}")"
OUT_DIR="${3:?output dir}"
CURRENT_SRCINFO="${4:-}"

PKGNAME=apps2samsung
TEMPLATE="$(dirname "$(readlink -f "$0")")/../aur/PKGBUILD"

if [[ ! "$VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
    echo "::error::version '$VERSION' must look like 2.7.9 (no v, no -beta)" >&2
    exit 1
fi

sum() {
    local f="$SOURCES_DIR/$1"
    if [[ ! -s "$f" ]]; then
        echo "::error::missing source file $f" >&2
        exit 1
    fi
    sha256sum "$f" | cut -d' ' -f1
}
SHA_LICENSE=$(sum "${PKGNAME}-${VERSION}-LICENSE")
SHA_ICON=$(sum "${PKGNAME}-${VERSION}.svg")
SHA_X64=$(sum "Apps2Samsung-v${VERSION}-linux-x64.tar.gz")
SHA_ARM64=$(sum "Apps2Samsung-v${VERSION}-linux-arm64.tar.gz")

# ---------------- pkgrel ----------------
PKGREL=1
CHANGED=true
if [[ -n "$CURRENT_SRCINFO" && -s "$CURRENT_SRCINFO" ]]; then
    field() { awk -v k="$1" '$1==k && $2=="=" {print $3; exit}' "$CURRENT_SRCINFO"; }
    CUR_VER=$(field pkgver)
    CUR_REL=$(field pkgrel)
    if [[ "$CUR_VER" == "$VERSION" ]]; then
        CUR_SUMS=$(awk '$1 ~ /^sha256sums/ {print $3}' "$CURRENT_SRCINFO" | sort | tr '\n' ' ')
        NEW_SUMS=$(printf '%s\n' "$SHA_LICENSE" "$SHA_ICON" "$SHA_X64" "$SHA_ARM64" | sort | tr '\n' ' ')
        if [[ "$CUR_SUMS" == "$NEW_SUMS" ]]; then
            PKGREL="$CUR_REL"
            CHANGED=false
            echo "AUR already has $PKGNAME $VERSION-$CUR_REL with these exact sources — nothing to publish"
        else
            PKGREL=$((CUR_REL + 1))
            echo "AUR has $VERSION-$CUR_REL with different checksums (assets rebuilt) — bumping to pkgrel $PKGREL"
        fi
    else
        echo "AUR has ${CUR_VER:-nothing}, publishing $VERSION-1"
    fi
else
    echo "No current .SRCINFO given — rendering $VERSION-1"
fi

# ---------------- render ----------------
mkdir -p "$OUT_DIR"
sed -e "s|@PKGVER@|$VERSION|g" \
    -e "s|@PKGREL@|$PKGREL|g" \
    -e "s|@SHA256_LICENSE@|$SHA_LICENSE|g" \
    -e "s|@SHA256_ICON@|$SHA_ICON|g" \
    -e "s|@SHA256_X86_64@|$SHA_X64|g" \
    -e "s|@SHA256_AARCH64@|$SHA_ARM64|g" \
    "$TEMPLATE" > "$OUT_DIR/PKGBUILD"

if grep -q '@[A-Z0-9_]\+@' "$OUT_DIR/PKGBUILD"; then
    echo "::error::unfilled placeholder in rendered PKGBUILD:" >&2
    grep -n '@[A-Z0-9_]\+@' "$OUT_DIR/PKGBUILD" >&2
    exit 1
fi
bash -n "$OUT_DIR/PKGBUILD"

echo "changed=$CHANGED"
echo "pkgrel=$PKGREL"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    { echo "changed=$CHANGED"; echo "pkgrel=$PKGREL"; } >> "$GITHUB_OUTPUT"
fi
