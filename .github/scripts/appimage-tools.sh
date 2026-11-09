#!/usr/bin/env bash
# Puts appimagetool on PATH and the type2 runtimes next to it, for package-linux.sh.
#
# GitHub release downloads 504 now and then, and appimagetool itself downloads the runtime on every
# run unless it is given --runtime-file, so one hiccup failed the whole Linux packaging job. Each
# file is tried fresh first, authenticated with GH_TOKEN when set and then anonymously; when both
# fail (or return something that is not an ELF binary) the copy committed in .github/tools/appimage
# is used instead.
set -euo pipefail

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../tools/appimage" && pwd)"

is_elf() { [ "$(head -c 4 "$1" | od -An -tx1 | tr -d ' \n')" = "7f454c46" ]; }

# fetch <owner/repo> <tag> <asset>   (saved as TOOLS_DIR/<asset>)
fetch() {
    local repo="$1" tag="$2" asset="$3" dest="$TOOLS_DIR/$3" tmp
    tmp="$(mktemp -d)"
    # 1. Authenticated through the API: GitHub sheds anonymous release-download traffic first under
    #    load (their 2026-11-09 incident), signed-in requests kept working.
    if [ -n "${GH_TOKEN:-}" ] && gh release download "$tag" -R "$repo" -p "$asset" -D "$tmp" 2>/dev/null \
        && is_elf "$tmp/$asset"; then
        mv "$tmp/$asset" "$dest"; echo "✔ $asset: downloaded (gh)"
    # 2. Plain anonymous download.
    elif curl -fsSL --retry 3 --retry-delay 5 --retry-all-errors --max-time 120 -o "$tmp/$asset" \
            "https://github.com/$repo/releases/download/$tag/$asset" && is_elf "$tmp/$asset"; then
        mv "$tmp/$asset" "$dest"; echo "✔ $asset: downloaded (curl)"
    # 3. The copy committed in the repo.
    elif [ -s "$dest" ]; then
        echo "::warning::$asset: download failed, using the committed copy"
    else
        echo "::error::$asset: download failed and there is no committed copy in $TOOLS_DIR" >&2
        exit 1
    fi
    rm -rf "$tmp"
    chmod +x "$dest"
}

fetch AppImage/appimagetool 1.9.1 appimagetool-x86_64.AppImage
fetch AppImage/type2-runtime continuous runtime-x86_64
fetch AppImage/type2-runtime continuous runtime-aarch64

sudo install -m 755 "$TOOLS_DIR/appimagetool-x86_64.AppImage" /usr/local/bin/appimagetool
