#!/usr/bin/env bash
# setup-nsis.sh — procura NSIS (makensis) in .tools/nsis/ quando non è installato, senza sudo e senza mingw.
#
# È la strada documentata da NSIS per macOS e Linux: si compila nativamente il solo compilatore (makensis, C++,
# pochi minuti) e si usano stub, plugin e librerie della distribuzione ufficiale per Windows, che sono già compilati.
# Serve un compilatore C++ (su macOS i Command Line Tools), zlib e python3 (per SCons, messo in un virtualenv).
# Gli archivi sono verificati con gli SHA-1 pubblicati su SourceForge.
#
# Stampa il percorso di makensis. Uso: MAKENSIS="$(scripts/setup-nsis.sh)"
set -euo pipefail

NSIS_VERSION="3.13"
SRC_SHA1="e0bd42648bea7bd933b32b9f1459cdb29ed41709"   # nsis-3.13-src.tar.bz2
WIN_SHA1="db14f8af2ea346b786c6ae19343f61aaf5e09b8e"   # nsis-3.13.zip

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS="$ROOT/.tools"
DEST="$TOOLS/nsis/nsis-$NSIS_VERSION"
MAKENSIS="$DEST/Bin/makensis"

if [[ -x "$MAKENSIS" ]]; then
    echo "$MAKENSIS"
    exit 0
fi

log() { echo "$@" >&2; }
BASE="https://downloads.sourceforge.net/project/nsis/NSIS%203/$NSIS_VERSION"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/setup-nsis.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

fetch() {
    local file="$1" sha="$2"
    log "==> scarico $file"
    curl -fsSL -o "$WORK/$file" "$BASE/$file"
    echo "$sha  $WORK/$file" | shasum -a 1 -c - >/dev/null || { log "SHA-1 non corrispondente per $file"; exit 1; }
}
fetch "nsis-$NSIS_VERSION-src.tar.bz2" "$SRC_SHA1"
fetch "nsis-$NSIS_VERSION.zip" "$WIN_SHA1"

log "==> estraggo"
mkdir -p "$TOOLS/nsis"
rm -rf "$DEST"
unzip -q "$WORK/nsis-$NSIS_VERSION.zip" -d "$TOOLS/nsis"
mkdir -p "$WORK/src"
tar -xjf "$WORK/nsis-$NSIS_VERSION-src.tar.bz2" -C "$WORK/src"

log "==> SCons in un virtualenv temporaneo"
python3 -m venv "$WORK/venv"
"$WORK/venv/bin/pip" install -q scons >&2

log "==> compilo makensis (solo il compilatore)"
(
    cd "$WORK/src/nsis-$NSIS_VERSION-src"
    "$WORK/venv/bin/scons" VERSION="$NSIS_VERSION" SKIPSTUBS=all SKIPPLUGINS=all SKIPUTILS=all SKIPMISC=all \
        NSIS_CONFIG_CONST_DATA_PATH=no PREFIX="$WORK/out" install-compiler >"$WORK/scons.log" 2>&1
) || { tail -20 "$WORK/scons.log" >&2; exit 1; }
# Con NSIS_CONFIG_CONST_DATA_PATH=no makensis cerca Stubs/, Include/, Contrib/ un livello sopra di sé, come
# Bin\makensis.exe nella distribuzione per Windows.
install -m 755 "$WORK/out/makensis" "$MAKENSIS"
"$MAKENSIS" -VERSION >/dev/null
log "==> pronto: $MAKENSIS"
echo "$MAKENSIS"
