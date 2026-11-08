@echo off
rem ------------------------------------------------------------------
rem  Compila WinUsage con il compilatore C# gia' presente in Windows
rem  (.NET Framework 4.x). Non serve installare nulla.
rem  Risultato: bin\WinUsage.exe
rem ------------------------------------------------------------------
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Compilatore C# non trovato.
  exit /b 1
)

if not exist bin mkdir bin

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:bin\WinUsage.exe ^
  /win32icon:app.ico /win32manifest:app.manifest /resource:app.ico,WinUsage.app.ico ^
  /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 (
  echo Compilazione fallita.
  exit /b 1
)
echo OK: bin\WinUsage.exe
