#!/usr/bin/env bash
# Build a release zip for Unity Mod Manager and Nexus Mods: dist/<Id>-<version>.zip holding one
# <Id> folder with exactly the files the mod needs (its assembly and yard layouts), its license
# and changelog. Builds without the development tools (-p:DevTools=false: the in-game
# inspector), under a memory cap.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dv="${DV_INSTALL_DIR:-$HOME/.local/share/Steam/steamapps/common/Derail Valley}"
info="$repo/src/Interchanges/Info.json"
id="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["Id"])' "$info")"
version="$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["Version"])' "$info")"
out="$repo/src/Interchanges/bin/Release/net48"
zipfile="$repo/dist/$id-$version.zip"
cap=(systemd-run --user --scope -q -p MemoryMax=6G -p MemorySwapMax=0)

echo "Packaging $id $version"
if ! build="$("${cap[@]}" dotnet build "$repo/src/Interchanges/Interchanges.csproj" -c Release -p:DvInstallDir="$dv" -p:DevTools=false --no-incremental -nologo 2>&1)"; then
    grep -E "error" <<<"$build" | sort -u >&2
    exit 1
fi
if grep -q -E "warning CS" <<<"$build"; then
    grep -E "warning CS" <<<"$build" | sort -u >&2
    exit 1
fi
# The inspector must not be in the release build.
if strings -el "$out/Interchanges.dll" | grep -q "inspect\.txt"; then
    echo "The release build still holds the inspector" >&2
    exit 1
fi

stage="$repo/dist/stage"
rm -rf "${stage:?}" "$zipfile"
mkdir -p "$stage/$id"
cp "$out/Info.json" "$out/Interchanges.dll" "$out"/layout-*.json "$stage/$id/"
cp "$repo/LICENSE" "$repo/CHANGELOG.md" "$stage/$id/"
(cd "$stage" && zip -q -X -r "$zipfile" "$id")
rm -rf "${stage:?}"
unzip -l "$zipfile"
sha256sum "$zipfile"
