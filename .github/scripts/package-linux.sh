#!/usr/bin/env bash
# Builds the Linux packages for one architecture from an already-published self-contained folder.
#
#   package-linux.sh <publish-dir> <arch: x64|arm64> <version> <asset-tag> <out-dir> <icon-png>
#
# Produces, named the way the release assets are (Apps2Samsung-v<version>-linux-<arch>.<ext>):
#   .tar.gz     the portable folder, for anything not covered below
#   .deb        Debian/Ubuntu/Mint
#   .rpm        Fedora/openSUSE/RHEL      (#589)
#   .AppImage   distro-independent, no root needed (#589)
#
# Lives here rather than inline in the release workflows because it is used by three callers — beta
# release, stable release, and the PR check that proves the packaging still builds. It used to be
# release-only, so a mistake in it could only ever be found by cutting a release.
set -euo pipefail

# Absolute paths throughout: rpmbuild runs its %install script from its own working directory, so a
# relative publish dir or icon would silently resolve to the wrong place.
PUBLISH_DIR="$(realpath "${1:?publish dir}")"
ARCH="${2:?arch (x64|arm64)}"
# Two version strings, deliberately: the package metadata version (rpm-legal, no '-') and the tag the
# asset file names carry, which for a beta is v2.7.9-beta. Deriving the file names from the version
# alone dropped the -beta marker from the beta release's Linux assets.
VERSION="${3:?package version, e.g. 2.7.9}"
ASSET_TAG="${4:?asset tag, e.g. v2.7.9-beta}"
mkdir -p "${5:?output dir}"
OUT_DIR="$(realpath "$5")"
ICON_PNG="$(realpath "${6:?256x256 png}")"

PRODUCT="Apps2Samsung"
TAG="$ASSET_TAG"
APP_ID="apps2samsung"

# rpm reserves '-' as the version/release separator, so it can never appear in Version:. Say that
# here rather than letting rpmbuild fail three formats later with "Illegal char '-'".
if [[ "$VERSION" == *-* ]]; then
    echo "::error::version '$VERSION' contains '-', which rpm does not allow (use 2.7.9, not v2.7.9-beta)" >&2
    exit 1
fi

case "$ARCH" in
    x64)   DEB_ARCH=amd64;  RPM_ARCH=x86_64;  APPIMAGE_ARCH=x86_64 ;;
    arm64) DEB_ARCH=arm64;  RPM_ARCH=aarch64; APPIMAGE_ARCH=aarch64 ;;
    *)     echo "::error::unknown arch '$ARCH'" >&2; exit 1 ;;
esac

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The app is self-contained, so every package ships the same tree; only the metadata differs.
chmod +x "$PUBLISH_DIR/$PRODUCT"

desktop_entry() {
    cat <<EOF
[Desktop Entry]
Type=Application
Name=Apps2Samsung
Comment=Install apps on Samsung Tizen TVs
Exec=$1
Icon=$APP_ID
Terminal=false
Categories=Utility;
EOF
}

# ---------------- tar.gz ----------------
tar -czf "$OUT_DIR/${PRODUCT}-${TAG}-linux-${ARCH}.tar.gz" -C "$PUBLISH_DIR" .
echo "✔ tar.gz"

# ---------------- .deb ----------------
DEB_ROOT="$WORK/deb"
mkdir -p "$DEB_ROOT/opt/$APP_ID" \
         "$DEB_ROOT/usr/share/applications" \
         "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps" \
         "$DEB_ROOT/DEBIAN"
cp -R "$PUBLISH_DIR/." "$DEB_ROOT/opt/$APP_ID/"
cp "$ICON_PNG" "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"
desktop_entry "/opt/$APP_ID/$PRODUCT" > "$DEB_ROOT/usr/share/applications/$APP_ID.desktop"
# No world-writable directories under /opt any more: the app writes downloads and logs to the
# per-user XDG directories instead (#589), so root-owned install files are fine.
cat > "$DEB_ROOT/DEBIAN/control" <<EOF
Package: $APP_ID
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: MadeByPatrick
Conflicts: jellyfin2samsung
Replaces: jellyfin2samsung
Provides: jellyfin2samsung
Description: Install apps on Samsung Tizen TVs
EOF
dpkg-deb --build "$DEB_ROOT" >/dev/null
mv "$WORK/deb.deb" "$OUT_DIR/${PRODUCT}-${TAG}-linux-${ARCH}.deb"
echo "✔ deb"

# ---------------- .rpm ----------------
# rpmbuild wants its own tree. The architecture comes from --target alone, NOT from a BuildArch in
# the spec: with BuildArch set, rpmbuild checks it against the host and refuses to build the aarch64
# package on an x86_64 runner ("No compatible architectures found for build"). Nothing is compiled
# here — the payload is already published for the target — so the target only tags the package.
RPM_TOP="$WORK/rpm"
mkdir -p "$RPM_TOP"/{BUILD,RPMS,SOURCES,SPECS,BUILDROOT}
cat > "$RPM_TOP/SPECS/$APP_ID.spec" <<EOF
# rpm's default post-install processing runs strip over every ELF it finds. Our payload is a .NET
# single-file bundle — the app is appended to the host executable — and stripping it threw away 126
# of its 138 MB, leaving a package that installs and then cannot run. It only showed up as an x64
# rpm suspiciously smaller than the aarch64 one, which survived because strip can't process a
# foreign-architecture binary. Turn the whole post-processing off; nothing here needs it.
%global __os_install_post %{nil}
%global __strip /bin/true
%global debug_package %{nil}

Name:           $APP_ID
Version:        $VERSION
Release:        1
Summary:        Install apps on Samsung Tizen TVs
License:        MIT
URL:            https://github.com/Apps2Samsung/Apps2Samsung
Provides:       jellyfin2samsung
# Versioned, or rpm warns: this replaces any jellyfin2samsung up to the rename.
Obsoletes:      jellyfin2samsung < $VERSION
# The payload is a self-contained .NET build: no runtime dependency to declare, and letting rpm
# auto-scan the bundled native libraries only invents requires the distro can't satisfy.
AutoReqProv:    no

%description
Apps2Samsung installs Jellyfin and other community apps onto Samsung Tizen TVs over the
developer-mode connection.

%install
mkdir -p %{buildroot}/opt/$APP_ID
cp -R $PUBLISH_DIR/. %{buildroot}/opt/$APP_ID/
mkdir -p %{buildroot}/usr/share/applications
cp $WORK/$APP_ID.desktop %{buildroot}/usr/share/applications/$APP_ID.desktop
mkdir -p %{buildroot}/usr/share/icons/hicolor/256x256/apps
cp $ICON_PNG %{buildroot}/usr/share/icons/hicolor/256x256/apps/$APP_ID.png

%files
/opt/$APP_ID
/usr/share/applications/$APP_ID.desktop
/usr/share/icons/hicolor/256x256/apps/$APP_ID.png

%changelog
EOF
desktop_entry "/opt/$APP_ID/$PRODUCT" > "$WORK/$APP_ID.desktop"
# Quiet unless it fails: rpmbuild narrates its whole %install script otherwise.
if ! rpmbuild --define "_topdir $RPM_TOP" --target "$RPM_ARCH" \
        -bb "$RPM_TOP/SPECS/$APP_ID.spec" > "$WORK/rpmbuild.log" 2>&1; then
    cat "$WORK/rpmbuild.log" >&2
    exit 1
fi
mv "$RPM_TOP/RPMS/$RPM_ARCH/$APP_ID-$VERSION-1.$RPM_ARCH.rpm" \
   "$OUT_DIR/${PRODUCT}-${TAG}-linux-${ARCH}.rpm"
echo "✔ rpm"

# ---------------- AppImage ----------------
# AppDir layout: the payload under usr/, with the icon and .desktop at the root where appimagetool
# looks for them. AppRun is a shell stub rather than a symlink so the binary keeps its own directory
# as the working dir (it loads Assets/ from there).
APP_DIR="$WORK/AppDir"
mkdir -p "$APP_DIR/usr/bin" "$APP_DIR/usr/share/applications" \
         "$APP_DIR/usr/share/icons/hicolor/256x256/apps"
cp -R "$PUBLISH_DIR/." "$APP_DIR/usr/bin/"
cp "$ICON_PNG" "$APP_DIR/$APP_ID.png"
cp "$ICON_PNG" "$APP_DIR/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"
desktop_entry "$PRODUCT" > "$APP_DIR/$APP_ID.desktop"
cp "$APP_DIR/$APP_ID.desktop" "$APP_DIR/usr/share/applications/$APP_ID.desktop"
cat > "$APP_DIR/AppRun" <<EOF
#!/bin/sh
HERE="\$(dirname "\$(readlink -f "\$0")")"
cd "\$HERE/usr/bin" || exit 1
exec ./$PRODUCT "\$@"
EOF
chmod +x "$APP_DIR/AppRun"

# appimagetool takes the target architecture from $ARCH (it embeds the matching runtime), and
# --appimage-extract-and-run avoids needing FUSE, which CI runners don't have.
env ARCH="$APPIMAGE_ARCH" "${APPIMAGETOOL:-appimagetool}" --appimage-extract-and-run \
    "$APP_DIR" "$OUT_DIR/${PRODUCT}-${TAG}-linux-${ARCH}.AppImage" >/dev/null
echo "✔ AppImage"

ls -la "$OUT_DIR"
