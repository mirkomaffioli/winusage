#!/bin/sh
# Collaudo su Linux con Mono: mappa winsqlite3.dll su libsqlite3 di sistema.
# Richiede: mono-mcs, libsqlite3-0. Opzionale: TZ=Europe/Rome per provare la mezzanotte locale.
set -e
cd "$(dirname "$0")"
OUT=${TMPDIR:-/tmp}/winusage-tests
mkdir -p "$OUT"
mcs -langversion:5 -out:"$OUT/tests.exe" -r:System.Drawing.dll ../src/Db.cs ../src/Core.cs ../src/UsageTracker.cs ../src/History.cs ../src/ChipTune.cs Tests.cs
cat > "$OUT/tests.exe.config" <<'XML'
<configuration>
  <dllmap dll="winsqlite3.dll" target="libsqlite3.so.0" />
</configuration>
XML
TZ=${TZ:-Europe/Rome} mono "$OUT/tests.exe"
