@echo off
rem ---------------------------------------------------------------------------
rem  Firma Authenticode di uno o piu' .exe su Windows con signtool (Windows SDK).
rem
rem  Uso:  sign.bat file.exe [altri.exe]     firma i file
rem        sign.bat --check                   esce con 0 se la firma e' configurata
rem
rem  Configurazione: variabili d'ambiente, oppure sign.env.bat accanto a questo
rem  script (MAI nel repository: e' in .gitignore). Basta UNA delle modalita':
rem
rem  1) File .pfx (certificato di prova)
rem       set SIGN_PFX=C:\percorso\certificato.pfx
rem       set SIGN_PFX_PASS=password
rem  2) Certificato nell'archivio di Windows o su token USB (YubiKey, SafeNet)
rem       set SIGN_THUMBPRINT=impronta SHA1 del certificato
rem  3) Azure Artifact Signing (solo organizzazioni nell'UE, e singoli in USA/Canada)
rem       set SIGN_DLIB=C:\...\Azure.CodeSigning.Dlib.dll
rem       set SIGN_DMDF=C:\...\metadata.json
rem
rem  Facoltativi: SIGN_TIMESTAMP (predefinito http://timestamp.digicert.com, "none" = senza),
rem               SIGN_NAME, SIGN_URL, SIGNTOOL (percorso di signtool.exe)
rem  Senza configurazione non fa niente: la build resta non firmata.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion
if exist "%~dp0sign.env.bat" call "%~dp0sign.env.bat"
if "%SIGN_TIMESTAMP%"=="" set SIGN_TIMESTAMP=http://timestamp.digicert.com
if "%SIGN_NAME%"=="" set SIGN_NAME=WinUsage
if "%SIGN_URL%"=="" set SIGN_URL=https://github.com/mirkomaffioli/winusage

set MODE=
if not "%SIGN_DLIB%"=="" set MODE=azure
if "%MODE%"=="" if not "%SIGN_THUMBPRINT%"=="" set MODE=store
if "%MODE%"=="" if not "%SIGN_PFX%"=="" set MODE=pfx

if "%~1"=="--check" (
  if "%MODE%"=="" exit /b 1
  exit /b 0
)
if "%MODE%"=="" (
  echo sign.bat: firma non configurata ^(vedi l'intestazione dello script^): file lasciati non firmati.
  exit /b 0
)

if "%SIGNTOOL%"=="" (
  for /f "delims=" %%S in ('dir /b /s "%ProgramFiles(x86)%\Windows Kits\10\bin\*signtool.exe" 2^>nul ^| findstr /i "\\x64\\"') do set SIGNTOOL=%%S
)
if "%SIGNTOOL%"=="" set SIGNTOOL=signtool.exe

set TS=/tr "%SIGN_TIMESTAMP%" /td sha256
if /i "%SIGN_TIMESTAMP%"=="none" set TS=

if "%MODE%"=="pfx" set WHO=/f "%SIGN_PFX%" /p "%SIGN_PFX_PASS%"
if "%MODE%"=="store" set WHO=/sha1 %SIGN_THUMBPRINT%
if "%MODE%"=="azure" set WHO=/dlib "%SIGN_DLIB%" /dmdf "%SIGN_DMDF%"

:next
if "%~1"=="" exit /b 0
"%SIGNTOOL%" sign /fd sha256 %WHO% %TS% /d "%SIGN_NAME%" /du "%SIGN_URL%" "%~1"
if errorlevel 1 (
  echo Firma non riuscita: %~1
  exit /b 1
)
echo firmato: %~1
shift
goto next
