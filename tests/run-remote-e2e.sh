#!/bin/sh
# Prova da capo a fondo del controllo remoto (RemoteE2E.cs): il vero RemoteService contro un server Telegram finto nello
# stesso processo. Riproduce i casi in cui la 1.6.0 perdeva i comandi o teneva ferme le risposte (circa 30 secondi).
# Richiede: mono-mcs, libmono-system-windows-forms4.0-cil, libsqlite3-0; senza un display usa xvfb-run (pacchetto xvfb).
# Per provare altri sorgenti (ad esempio una versione vecchia): sh run-remote-e2e.sh /percorso/della/cartella/src
set -e
cd "$(dirname "$0")"
SRC=${1:-../src}
OUT=${TMPDIR:-/tmp}/winusage-e2e
mkdir -p "$OUT"
set --
for f in "$SRC"/*.cs; do
  case "$f" in
    */Program.cs|*/TrayContext.cs) ;;   # Main e icona nella tray: la prova ha i suoi
    *) set -- "$@" "$f" ;;
  esac
done
mcs -langversion:5 -out:"$OUT/e2e.exe" \
  -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll -r:System.Security.dll \
  "$@" FakeTelegram.cs RemoteE2E.cs
cat > "$OUT/e2e.exe.config" <<'XML'
<configuration>
  <dllmap dll="winsqlite3.dll" target="libsqlite3.so.0" />
</configuration>
XML
if [ -z "$DISPLAY" ] && command -v xvfb-run >/dev/null 2>&1; then
  exec xvfb-run -a mono "$OUT/e2e.exe"
fi
exec mono "$OUT/e2e.exe"
