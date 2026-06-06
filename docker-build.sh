#!/usr/bin/env bash
#
# Build Jellyfin2Samsung from source inside the .NET 8 SDK container.
# No host dotnet install needed. Output is owned by the current user.
#
# Usage:
#   ./docker-build.sh          build only
#   ./docker-build.sh --run    build, then launch the app
#
set -euo pipefail

IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
PROJECT="Jellyfin2Samsung-CrossOS/Jellyfin2Samsung.csproj"
BIN="Jellyfin2Samsung-CrossOS/bin/Release/net8.0/linux-x64/publish/Jellyfin2Samsung"

# Repo root = dir this script lives in.
cd "$(dirname "$(readlink -f "$0")")"

command -v docker >/dev/null || { echo "docker not found on PATH"; exit 1; }

# Clean prior output. Run as root inside the container so we can also remove
# any root-owned bin/obj left by an older build.
echo ">> cleaning bin/obj"
docker run --rm -v "$PWD":/src -w /src "$IMAGE" \
  rm -rf Jellyfin2Samsung-CrossOS/bin Jellyfin2Samsung-CrossOS/obj

# Build as the current user so output isn't root-owned.
# HOME=/tmp gives the non-root user a writable home for the NuGet restore.
echo ">> building"
docker run --rm \
  -v "$PWD":/src -w /src \
  --user "$(id -u):$(id -g)" \
  -e HOME=/tmp \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$IMAGE" \
  dotnet publish "$PROJECT" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false

echo ">> done: $BIN"

if [[ "${1:-}" == "--run" ]]; then
  echo ">> launching"
  exec "./$BIN"
fi
