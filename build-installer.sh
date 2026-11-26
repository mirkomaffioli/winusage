#!/bin/sh
# Crea dist/WinUsage-Setup-<versione>.exe da Linux/macOS (richiede: nsis, e l'exe in bin/).
# Se l'exe non c'e' lo compila con Mono (build-mono.sh).
# Se la firma e' configurata (sign.sh / sign.env) firma l'exe, il programma di disinstallazione e l'installer.
set -e
cd "$(dirname "$0")"
[ -f bin/WinUsage.exe ] || bash build-mono.sh
mkdir -p dist
if sh sign.sh --check; then
  chmod +x sign.sh 2>/dev/null || true   # NSIS lo esegue direttamente (il percorso può contenere spazi)
  sh sign.sh bin/WinUsage.exe
  (cd installer && makensis -V2 "-DSIGN=$(cd .. && pwd)/sign.sh" WinUsage.nsi)
else
  echo "Firma non configurata: installer non firmato."
  (cd installer && makensis -V2 WinUsage.nsi)
fi
ls -la dist
