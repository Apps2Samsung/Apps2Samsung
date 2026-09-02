#!/usr/bin/env bash
#
# Android versionCode — one source of truth for both release channels.
#
# versionCode is the only number Android compares when deciding whether an install
# is an upgrade. It must never go down on *either* channel, or users get
#
#     Cannot install an older version of an app (versionCode 273 -> 58)
#
# and are stuck with what they have until they uninstall (losing their settings).
#
# Both channels derive it from the same repo-wide, monotonically growing number —
# the commit count — plus a small per-channel offset:
#
#     beta   = commits * 10 + 0
#     stable = commits * 10 + 9
#
# Why it cannot invert:
#   * the commit count is a property of the REPO, so both workflows compute the
#     same base for the same commit;
#   * stable (+9) outranks the beta built from that same commit, so beta -> stable
#     is always an upgrade;
#   * a beta cut after that stable sits on at least one more commit, i.e. at least
#     +10, so it outranks that stable too — stable -> beta is also an upgrade.
#
# What went wrong before: both workflows used ${{ github.run_number }}, which
# counts runs PER WORKFLOW FILE, not per repo. Beta reached 298 while stable was
# still on 58, so every beta user hit the dialog above when moving to stable.
#
# Usage:
#   android-version-code.sh compute beta|stable
#       Prints the versionCode and exports it as ANDROID_VERSION_CODE (through
#       $GITHUB_ENV / $GITHUB_OUTPUT when running in Actions). Refuses to hand back
#       a code that would downgrade anything already published.
#
#   android-version-code.sh verify <apk> [expected-code]
#       Asserts the built APK really carries that code — an MSBuild or .csproj
#       change must not be able to quietly override it — and logs the signing
#       certificate. Set EXPECTED_SIGNER_SHA256 to also pin the signing key.
#
# Both subcommands also run locally, e.g.:
#   .github/scripts/android-version-code.sh compute stable
#
set -euo pipefail

# Distance between two consecutive commits' codes. Also the width of the "same
# commit, other channel" window that compute is allowed to step over (see below).
STRIDE=10

# Offsets must be < STRIDE, and stable must be the highest, so that stable
# outranks every beta built from the same commit.
OFFSET_BETA=0
OFFSET_STABLE=9

# A shallow checkout reports 1 commit, which would produce versionCode 10 and
# downgrade every install out there. Anything under this means the job lost its
# `fetch-depth: 0` on actions/checkout.
MIN_COMMITS=100

# High-water mark of the retired github.run_number scheme (beta was at 298 when it
# was replaced). Any code at or below this is a downgrade for somebody, so it is a
# hard floor. It also covers every release published before this script existed,
# which is why the published-release check below only has to look at the newest
# release per channel.
LEGACY_FLOOR=1000

# Android's own ceiling for versionCode.
ANDROID_MAX_CODE=2100000000

log()  { printf '%s\n' "$*"; }
warn() { if [ -n "${GITHUB_ACTIONS:-}" ]; then printf '::warning::%s\n' "$*"; else printf 'WARNING: %s\n' "$*"; fi; }
die()  { if [ -n "${GITHUB_ACTIONS:-}" ]; then printf '::error::%s\n' "$*"; else printf 'ERROR: %s\n' "$*"; fi; exit 1; }

WORKDIR=""
cleanup() { if [ -n "$WORKDIR" ]; then rm -rf "$WORKDIR"; fi; }
trap cleanup EXIT

# Locate a build-tools binary (aapt2, apksigner). The GitHub runners ship the
# Android SDK but do not put build-tools on PATH, and the version bundled in the
# image changes, so pick the newest one actually installed.
find_sdk_tool() {
  local name="$1" sdk found
  for sdk in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" "$HOME/Android/Sdk" /usr/local/lib/android/sdk; do
    if [ -n "$sdk" ] && [ -d "$sdk/build-tools" ]; then
      found=$(find "$sdk/build-tools" -maxdepth 2 -name "$name" -type f 2>/dev/null | sort -V | tail -n1 || true)
      if [ -n "$found" ]; then printf '%s\n' "$found"; return 0; fi
    fi
  done
  command -v "$name" 2>/dev/null || return 1
}

# Read one field out of `aapt2 dump badging`. Tolerates a failing aapt2 and prints
# nothing in that case — callers validate the result. `sed -n 1p` rather than
# `head -n1` so nothing closes the pipe early and trips `pipefail`.
badging_field() {
  local aapt2="$1" apk="$2" field="$3"
  { "$aapt2" dump badging "$apk" 2>/dev/null || true; } \
    | sed -n "s/.*${field}='\([^']*\)'.*/\1/p" | sed -n 1p
}

# Highest versionCode already published on GitHub Releases -> HIGHEST/HIGHEST_TAG.
#
# Codes are monotone by construction, so the highest one lives in the newest
# release of one of the two channels — two small downloads instead of trawling the
# whole release history. Everything older is covered by LEGACY_FLOOR.
HIGHEST=0
HIGHEST_TAG="(none)"
compute_highest_published_code() {
  local gh_args=() newest tag apk code aapt2

  command -v gh >/dev/null 2>&1 || die "gh CLI not found — cannot check published versionCodes"
  if [ -n "${GITHUB_REPOSITORY:-}" ]; then gh_args=(--repo "$GITHUB_REPOSITORY"); fi

  # Newest non-draft prerelease (beta) and newest non-draft release (stable).
  if ! newest=$(gh release list "${gh_args[@]+"${gh_args[@]}"}" --limit 60 \
        --json tagName,isPrerelease,isDraft \
        --jq '[ .[] | select(.isDraft == false and .isPrerelease == true)  ][0].tagName // empty,
               [ .[] | select(.isDraft == false and .isPrerelease == false) ][0].tagName // empty' 2>&1); then
    die "could not list releases (\`gh release list\` failed): $newest"
  fi

  aapt2=$(find_sdk_tool aapt2) || die "aapt2 not found — cannot read published versionCodes"

  if [ -z "$newest" ]; then
    log "  no published releases yet"
    return 0
  fi

  WORKDIR=$(mktemp -d)
  for tag in $newest; do
    rm -f "$WORKDIR"/*.apk
    if ! gh release download "$tag" "${gh_args[@]+"${gh_args[@]}"}" \
          -p '*-android.apk' -D "$WORKDIR" --clobber >/dev/null 2>&1; then
      log "  $tag -> no android APK asset, skipped"
      continue
    fi
    for apk in "$WORKDIR"/*.apk; do
      [ -f "$apk" ] || continue
      code=$(badging_field "$aapt2" "$apk" versionCode)
      case "$code" in
        ''|*[!0-9]*)
          log "  $tag -> could not read a versionCode from $(basename "$apk"), skipped"
          continue ;;
      esac
      log "  $tag -> versionCode $code"
      if [ "$code" -gt "$HIGHEST" ]; then HIGHEST=$code; HIGHEST_TAG=$tag; fi
    done
  done
  rm -rf "$WORKDIR"; WORKDIR=""
}

cmd_compute() {
  local channel="${1:-}" offset commits code behind

  case "$channel" in
    beta)   offset=$OFFSET_BETA ;;
    stable) offset=$OFFSET_STABLE ;;
    *) die "usage: $0 compute beta|stable" ;;
  esac

  commits=$(git rev-list --count HEAD)
  if [ "$commits" -lt "$MIN_COMMITS" ]; then
    die "commit count $commits looks like a shallow checkout — this job needs 'fetch-depth: 0' on actions/checkout"
  fi

  code=$(( commits * STRIDE + offset ))
  log "channel=$channel commits=$commits offset=$offset -> candidate versionCode $code"

  if [ "$code" -le "$LEGACY_FLOOR" ]; then
    die "versionCode $code is at or below the retired run_number scheme's high-water mark ($LEGACY_FLOOR) — publishing it would downgrade existing installs"
  fi

  log "versionCodes already published on GitHub Releases:"
  compute_highest_published_code
  log "highest published:  $HIGHEST ($HIGHEST_TAG)"

  if [ "$code" -le "$HIGHEST" ]; then
    behind=$(( HIGHEST - code ))
    if [ "$behind" -lt "$STRIDE" ]; then
      # The one case the formula cannot rank on its own: building one channel from
      # the very same commit the other channel was already published from —
      # typically cutting a beta straight after a stable with no commit in
      # between. Step over the published code instead of failing the release; the
      # result is still strictly increasing, and the next commit's code is higher
      # again either way.
      code=$(( HIGHEST + 1 ))
      warn "candidate versionCode was $behind behind the published $HIGHEST ($HIGHEST_TAG — same commit, other channel), so using $code instead. Land a commit before re-cutting this channel to keep codes aligned with the commit count."
    else
      die "versionCode $code is $behind behind the highest published code ($HIGHEST in $HIGHEST_TAG) — users on that build could not update, so refusing to publish. This normally means the release is building an older ref or a stale checkout; release from the tip of the branch instead."
    fi
  fi

  if [ "$code" -gt "$ANDROID_MAX_CODE" ]; then
    die "versionCode $code exceeds Android's maximum ($ANDROID_MAX_CODE)"
  fi

  log "==> versionCode $code"
  if [ -n "${GITHUB_ENV:-}" ]; then    echo "ANDROID_VERSION_CODE=$code" >> "$GITHUB_ENV"; fi
  if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "version-code=$code" >> "$GITHUB_OUTPUT"; fi
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "Android \`versionCode\` for **$channel**: \`$code\` (commits=$commits, offset=$offset)" >> "$GITHUB_STEP_SUMMARY"
  fi
}

cmd_verify() {
  local apk="${1:-}" expected="${2:-${ANDROID_VERSION_CODE:-}}"
  local aapt2 apksigner actual name fingerprint expected_signer

  [ -n "$apk" ] || die "usage: $0 verify <apk> [expected-code]"
  [ -f "$apk" ] || die "APK not found: $apk"
  [ -n "$expected" ] || die "no expected versionCode given and ANDROID_VERSION_CODE is not set"

  aapt2=$(find_sdk_tool aapt2) || die "aapt2 not found — cannot verify the APK"
  actual=$(badging_field "$aapt2" "$apk" versionCode)
  name=$(badging_field "$aapt2" "$apk" versionName)

  log "apk:                  $apk"
  log "versionName:          ${name:-(none)}"
  log "expected versionCode: $expected"
  log "actual   versionCode: ${actual:-(unreadable)}"

  [ -n "$actual" ] || die "could not read a versionCode from $apk"
  if [ "$actual" != "$expected" ]; then
    die "APK versionCode is $actual but should be $expected — something is overriding ApplicationVersion (check Apps2Samsung.Mobile/Apps2Samsung.Mobile.csproj)"
  fi
  if [ -z "$name" ]; then
    warn "APK has no versionName — ApplicationDisplayVersion did not make it into the build"
  fi

  # A signing-key change breaks in-place updates just as hard as a versionCode
  # downgrade, only with a different dialog ("App not installed"). Log the
  # certificate so it stays greppable across runs when a user reports that.
  if apksigner=$(find_sdk_tool apksigner); then
    fingerprint=$({ "$apksigner" verify --print-certs "$apk" 2>/dev/null || true; } \
      | sed -n 's/.*SHA-256 digest: *\([0-9a-fA-F]*\).*/\1/p' | sed -n 1p \
      | tr 'A-F' 'a-f')
    log "signer SHA-256:       ${fingerprint:-(unreadable)}"
    if [ -n "${EXPECTED_SIGNER_SHA256:-}" ]; then
      # Accept the value with or without the colons keytool/openssl like to print.
      expected_signer=$(printf '%s' "$EXPECTED_SIGNER_SHA256" | tr -d ': ' | tr 'A-F' 'a-f')
      [ -n "$fingerprint" ] || die "could not read the signing certificate of $apk, but EXPECTED_SIGNER_SHA256 is set"
      if [ "$fingerprint" != "$expected_signer" ]; then
        die "APK is signed with $fingerprint but must be signed with $expected_signer — Android refuses an update signed by a different key, so existing installs would be stuck. The ANDROID_KEYSTORE_* secrets were probably replaced; restore the original keystore."
      fi
      log "signer matches the pinned release certificate"
    fi
  else
    warn "apksigner not found — skipped the signing-certificate check"
  fi

  log "✅ APK carries versionCode $expected"
}

case "${1:-}" in
  compute) shift; cmd_compute "$@" ;;
  verify)  shift; cmd_verify  "$@" ;;
  *) die "usage: $0 {compute beta|stable | verify <apk> [expected-code]}" ;;
esac
