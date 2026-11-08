@echo off
rem ------------------------------------------------------------------
rem  Crea dist\WinUsage-Setup-<versione>.exe
rem  Richiede NSIS 3 (https://nsis.sourceforge.io) installato.
rem ------------------------------------------------------------------
setlocal
cd /d "%~dp0"

set MAKENSIS=makensis.exe
if exist "%ProgramFiles(x86)%\NSIS\makensis.exe" set MAKENSIS=%ProgramFiles(x86)%\NSIS\makensis.exe
if exist "%ProgramFiles%\NSIS\makensis.exe" set MAKENSIS=%ProgramFiles%\NSIS\makensis.exe

call build.bat
if errorlevel 1 exit /b 1

if not exist dist mkdir dist
pushd installer
"%MAKENSIS%" /V2 WinUsage.nsi
if errorlevel 1 (
  popd
  echo Creazione dell'installer fallita. NSIS e' installato?
  exit /b 1
)
popd
echo OK: dist\WinUsage-Setup-1.1.0.exe
