#!/bin/sh
# Compila WinUsage da Linux/macOS con Mono (pacchetto mono-mcs + libmono-system-windows-forms4.0-cil).
# L'exe risultante gira su Windows 11 con .NET Framework 4.8 (incluso in Windows).
set -e
cd "$(dirname "$0")"
mkdir -p bin
mcs -langversion:5 -target:winexe -optimize+ -out:bin/WinUsage.exe \
  -win32icon:app.ico -win32manifest:app.manifest -resource:app.ico,WinUsage.app.ico \
  -r:System.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll \
  src/*.cs
echo "OK: bin/WinUsage.exe"
