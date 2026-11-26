#!/bin/sh
# ---------------------------------------------------------------------------
#  Firma Authenticode di uno o più .exe da Linux/macOS (osslsigncode o jsign).
#
#  Uso:  sh sign.sh file.exe [altri.exe]      firma i file (sovrascrive)
#        sh sign.sh --check                    esce con 0 se la firma è configurata
#
#  Configurazione: variabili d'ambiente, oppure un file sign.env accanto a questo
#  script (MAI nel repository: è in .gitignore). Basta UNA delle tre modalità:
#
#  1) File .pfx/.p12 (certificato di prova, o vecchi certificati esportabili)
#       SIGN_PFX=/percorso/certificato.pfx
#       SIGN_PFX_PASS=password            (se manca viene chiesta)
#
#  2) Token USB (YubiKey, SafeNet...) tramite PKCS#11
#       SIGN_PKCS11_MODULE=/usr/lib/x86_64-linux-gnu/libykcs11.so
#       SIGN_PKCS11_KEY="pkcs11:object=..."   (URI della chiave sul token)
#       SIGN_CERT=/percorso/certificato.pem   (catena del certificato)
#
#  3) Firma nel cloud con jsign (https://ebourg.github.io/jsign): SSL.com eSigner,
#     Azure Key Vault / Artifact Signing, DigiCert ONE, Google Cloud KMS, ...
#       SIGN_JSIGN="--storetype ESIGNER --storepass ... --alias ..."
#
#  Facoltativi:
#       SIGN_TIMESTAMP=http://timestamp.digicert.com   (marca temporale RFC 3161: la firma
#                                                       resta valida dopo la scadenza del certificato;
#                                                       "none" = senza, solo per le prove)
#       SIGN_NAME="WinUsage"   SIGN_URL="https://github.com/mirkomaffioli/winusage"
#
#  Senza configurazione non fa niente: la build resta non firmata.
# ---------------------------------------------------------------------------
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
[ -f "$DIR/sign.env" ] && . "$DIR/sign.env"

: "${SIGN_TIMESTAMP:=http://timestamp.digicert.com}"
: "${SIGN_NAME:=WinUsage}"
: "${SIGN_URL:=https://github.com/mirkomaffioli/winusage}"

mode=""
if [ -n "$SIGN_JSIGN" ]; then mode=jsign
elif [ -n "$SIGN_PKCS11_MODULE" ]; then mode=pkcs11
elif [ -n "$SIGN_PFX" ]; then mode=pfx
fi

if [ "$1" = "--check" ]; then
  [ -n "$mode" ] && exit 0 || exit 1
fi
if [ -z "$mode" ]; then
  echo "sign.sh: firma non configurata (vedi l'intestazione dello script): file lasciati non firmati." >&2
  exit 0
fi
[ $# -gt 0 ] || { echo "Uso: sh sign.sh file.exe [altri.exe]" >&2; exit 2; }

if [ "$mode" = pfx ] && [ -z "$SIGN_PFX_PASS" ]; then
  printf "Password del certificato %s: " "$SIGN_PFX" >&2
  stty -echo 2>/dev/null || true; read -r SIGN_PFX_PASS; stty echo 2>/dev/null || true; echo >&2
fi

ts=""
[ "$SIGN_TIMESTAMP" != none ] && ts="$SIGN_TIMESTAMP"

for f in "$@"; do
  [ -f "$f" ] || { echo "sign.sh: $f non esiste" >&2; exit 1; }
  case "$mode" in
    jsign)
      # shellcheck disable=SC2086
      if [ -n "$ts" ]; then jsign $SIGN_JSIGN --tsaurl "$ts" --tsmode RFC3161 --name "$SIGN_NAME" --url "$SIGN_URL" "$f"
      else jsign $SIGN_JSIGN --name "$SIGN_NAME" --url "$SIGN_URL" "$f"; fi
      ;;
    pfx|pkcs11)
      tmp="$f.signing"
      rm -f "$tmp"
      if [ "$mode" = pfx ]; then
        osslsigncode sign -pkcs12 "$SIGN_PFX" -pass "$SIGN_PFX_PASS" \
          -h sha256 -n "$SIGN_NAME" -i "$SIGN_URL" ${ts:+-ts "$ts"} -in "$f" -out "$tmp" >/dev/null
      else
        osslsigncode sign -pkcs11module "$SIGN_PKCS11_MODULE" -key "$SIGN_PKCS11_KEY" -certs "$SIGN_CERT" \
          -h sha256 -n "$SIGN_NAME" -i "$SIGN_URL" ${ts:+-ts "$ts"} -in "$f" -out "$tmp" >/dev/null
      fi
      mv -f "$tmp" "$f"
      ;;
  esac
  echo "firmato: $f"
done
