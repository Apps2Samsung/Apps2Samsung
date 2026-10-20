#!/usr/bin/env bash
#
# The version Apple's Info.plist fields accept, from a release version.
#
# CFBundleShortVersionString and CFBundleVersion are both "one to three period-separated
# integers" (Apple's Information Property List reference). Our releases don't always fit that:
# stable versions have had four parts (v2.3.1.1) and the beta marker adds a suffix (3.0.0-beta).
# iOS refuses or misreads those, and SideStore compares them to its source to offer updates.
#
# So the shown version is the first three numeric parts, zero-padded:
#
#     3.0.0 -> 3.0.0     2.3.1.1 -> 2.3.1     3.0 -> 3.0.0     v3.0.0-beta -> 3.0.0
#
# Two releases that only differ in the fourth part (or beta vs stable of one version) then show
# the same version, so CFBundleVersion must tell them apart: the IPA jobs pass the Android
# versionCode for it (.github/scripts/android-version-code.sh), which already grows across both
# channels with every release.
#
# Usage: apple-version.sh <version>      prints the three-part version, fails on anything else
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: apple-version.sh <version>" >&2
  exit 2
fi

if [[ ! "$1" =~ ^v?([0-9]+)(\.([0-9]+))?(\.([0-9]+))?(\.[0-9]+)*(-.*)?$ ]]; then
  echo "❌ '$1' is not a release version (expected e.g. 3.0.0, 2.3.1.1 or 3.0.0-beta)" >&2
  exit 1
fi

# 10#: a leading zero must not read as octal; it also drops the zero (Apple wants plain integers).
echo "$((10#${BASH_REMATCH[1]})).$((10#${BASH_REMATCH[3]:-0})).$((10#${BASH_REMATCH[5]:-0}))"
