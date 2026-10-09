#!/usr/bin/env bash
# build.sh — verifiche, pubblicazione e installer Windows, anche da macOS o Linux (cross-compilazione).
#
#   scripts/build.sh            verifiche + build/publish/activity-tracker.exe + dist/e-track-agent-windows-setup.exe
#   SKIP_CHECKS=1 scripts/build.sh   salta le verifiche
#
# Serve: .NET SDK 10 (dotnet nel PATH, oppure in ~/.dotnet). NSIS 3 (makensis) se c'è nel PATH o in $MAKENSIS,
# altrimenti scripts/setup-nsis.sh lo procura in .tools/ (verificato con SHA-1, senza sudo).
# Su Windows c'è anche scripts/build.ps1.
# L'installer NON è firmato: al primo avvio SmartScreen chiede "Ulteriori informazioni" → "Esegui comunque".
set -euo pipefail

APP_NAME="activity-tracker"
# Nome del file che l'utente scarica (prodotto: «e-track agent»); progetto e eseguibile interno restano activity-tracker.
SETUP_NAME="e-track-agent-windows-setup.exe"
RID="win-x64"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PUBLISH_DIR="$ROOT/build/publish"
DIST_DIR="$ROOT/dist"

if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
fi
command -v dotnet >/dev/null || { echo "manca il .NET SDK 10 (https://dot.net)" >&2; exit 1; }
if [[ -z "${MAKENSIS:-}" ]]; then
    MAKENSIS="$(command -v makensis || true)"
    [[ -n "$MAKENSIS" ]] || MAKENSIS="$("$SCRIPT_DIR/setup-nsis.sh")"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
SETUP="$DIST_DIR/$SETUP_NAME"

echo "==> icone"
if [[ ! -f "$ROOT/assets/activity-tracker.ico" ]]; then
    python3 -I "$SCRIPT_DIR/make-icon.py"
else
    echo "    già presenti in assets/ (rigenerale con scripts/make-icon.py)"
fi

if [[ "${SKIP_CHECKS:-0}" != "1" ]]; then
    echo "==> verifiche (motore, regole, formato, storage, sync con server finto)"
    dotnet run --project "$ROOT/tests/ActivityTracker.Checks" -c Release | tail -n 1
fi

echo "==> dotnet publish ($RID, autosufficiente, un solo .exe)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$ROOT/src/ActivityTracker.Windows" -c Release -r "$RID" -o "$PUBLISH_DIR" -nologo -v quiet

echo "==> installer NSIS"
mkdir -p "$DIST_DIR"
rm -f "$SETUP"
"$MAKENSIS" -V2 -DVERSION="$VERSION" -DSOURCE_DIR="$PUBLISH_DIR" -DOUT_FILE="$SETUP" "$ROOT/installer/activity-tracker.nsi"
cp "$ROOT/installer/LEGGIMI.txt" "$DIST_DIR/LEGGIMI.txt"

echo "==> verifica"
[[ -f "$SETUP" ]]
head -c 2 "$SETUP" | grep -q "MZ" || { echo "l'installer non è un eseguibile Windows" >&2; exit 1; }
if command -v shasum >/dev/null; then (cd "$DIST_DIR" && shasum -a 256 "$(basename "$SETUP")" > "$(basename "$SETUP").sha256"); fi

size() { du -h "$1" | cut -f1 | tr -d ' '; }
echo "==> pronto"
echo "    app:       $PUBLISH_DIR/$APP_NAME.exe ($(size "$PUBLISH_DIR/$APP_NAME.exe"))"
echo "    installer: $SETUP ($(size "$SETUP"))"
