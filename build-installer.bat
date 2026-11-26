@echo off
rem ------------------------------------------------------------------
rem  Crea dist\WinUsage-Setup-<versione>.exe
rem  Richiede NSIS 3.08 o successivo (https://nsis.sourceforge.io) installato.
rem  Se la firma e' configurata (sign.bat / sign.env.bat) firma exe, disinstallazione e installer.
rem ------------------------------------------------------------------
setlocal
cd /d "%~dp0"

set MAKENSIS=makensis.exe
if exist "%ProgramFiles(x86)%\NSIS\makensis.exe" set MAKENSIS=%ProgramFiles(x86)%\NSIS\makensis.exe
if exist "%ProgramFiles%\NSIS\makensis.exe" set MAKENSIS=%ProgramFiles%\NSIS\makensis.exe

call build.bat
if errorlevel 1 exit /b 1

if not exist dist mkdir dist
set SIGNDEF=
call sign.bat --check
if not errorlevel 1 (
  call sign.bat bin\WinUsage.exe
  if errorlevel 1 exit /b 1
  set SIGNDEF="/DSIGN=%~dp0sign.bat"
)
pushd installer
"%MAKENSIS%" /V2 %SIGNDEF% WinUsage.nsi
if errorlevel 1 (
  popd
  echo Creazione dell'installer fallita. NSIS e' installato?
  exit /b 1
)
popd
echo OK: installer in dist\
