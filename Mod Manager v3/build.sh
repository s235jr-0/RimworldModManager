#!/usr/bin/env bash
# RimWorld Mod Manager v3 - build for Linux.
# Run from this folder:  ./build.sh
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "ERROR: the .NET 10 SDK was not found. See https://learn.microsoft.com/dotnet/core/install/linux"
    exit 1
fi

echo "Running tests..."
dotnet test

echo "Building a single RimModManager binary (includes .NET, no install needed)..."
dotnet publish src/RimModManager.App -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
    -o publish/linux-x64

# Debug symbols bundled by the graphics library; not needed to run.
rm -f publish/linux-x64/*.pdb

echo
echo "DONE: publish/linux-x64/RimModManager"
echo "SteamCMD on Linux needs 32-bit libraries, e.g. on Debian/Ubuntu:"
echo "  sudo apt install lib32gcc-s1"
