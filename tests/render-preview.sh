#!/bin/sh
# Disegna su PNG il toast pixel art e il grafico dello storico (richiede mono-mcs e libgdiplus).
# Uso: sh tests/render-preview.sh [cartella-di-uscita]
set -e
cd "$(dirname "$0")"
OUT=${1:-"$(pwd)/../docs"}
mkdir -p "$OUT"
mcs -langversion:5 -r:System.Drawing.dll -out:"$OUT/render.exe" \
  ../src/Art.cs ../src/Sprites.cs ../src/Core.cs ../src/Db.cs ../src/History.cs RenderPreview.cs
mono "$OUT/render.exe" "$OUT"
rm -f "$OUT/render.exe"
