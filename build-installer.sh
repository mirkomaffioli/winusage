#!/bin/sh
# Crea dist/WinUsage-Setup-<versione>.exe da Linux/macOS (richiede: nsis, e l'exe in bin/).
# Se l'exe non c'e' lo compila con Mono (build-mono.sh).
set -e
cd "$(dirname "$0")"
[ -f bin/WinUsage.exe ] || bash build-mono.sh
mkdir -p dist
(cd installer && makensis -V2 WinUsage.nsi)
ls -la dist
