#!/bin/sh
# Crea un certificato di firma AUTOFIRMATO, solo per provare la firma (certs/, ignorata da git).
# Windows non si fida di questo certificato: SmartScreen continua ad avvisare. Serve solo a verificare
# che gli script di firma funzionino. Poi:  SIGN_PFX=certs/test-codesign.pfx SIGN_PFX_PASS=prova sh build-installer.sh
set -e
cd "$(dirname "$0")/.."
mkdir -p certs
openssl req -x509 -newkey rsa:3072 -sha256 -days 30 -nodes \
  -keyout certs/test-codesign.key -out certs/test-codesign.pem \
  -subj "/CN=WinUsage TEST (non attendibile)" \
  -addext "keyUsage=critical,digitalSignature" -addext "extendedKeyUsage=codeSigning" 2>/dev/null
openssl pkcs12 -export -inkey certs/test-codesign.key -in certs/test-codesign.pem \
  -out certs/test-codesign.pfx -passout pass:prova -name "WinUsage TEST"
echo "Creato certs/test-codesign.pfx (password: prova), valido 30 giorni."
