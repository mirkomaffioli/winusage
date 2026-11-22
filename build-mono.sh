#!/bin/sh
# Compila WinUsage da Linux/macOS con Mono (pacchetti mono-mcs e mono-devel).
# L'exe risultante gira su Windows 11 con .NET Framework 4.8 (incluso in Windows).
#
# Si compila contro gli assembly di riferimento di .NET Framework 4.8 (/usr/lib/mono/4.8-api, pacchetto mono-devel),
# non contro le librerie di Mono: alcune classi stanno in DLL diverse (ad esempio Queue<T> è in mscorlib in Mono e in
# System.dll in .NET Framework) e un exe compilato contro Mono su Windows si chiude all'avvio con TypeLoadException.
set -e
cd "$(dirname "$0")"
REF=/usr/lib/mono/4.8-api
if [ ! -f "$REF/mscorlib.dll" ]; then
  echo "Manca $REF: installa il pacchetto mono-devel (servono gli assembly di riferimento di .NET 4.8)." >&2
  exit 1
fi
mkdir -p bin
mcs -langversion:5 -target:winexe -optimize+ -out:bin/WinUsage.exe \
  -nostdlib -lib:"$REF" -r:"$REF/mscorlib.dll" \
  -win32icon:app.ico -win32manifest:app.manifest -resource:app.ico,WinUsage.app.ico \
  -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll \
  -r:System.Web.Extensions.dll -r:System.Security.dll \
  src/*.cs
echo "OK: bin/WinUsage.exe"
