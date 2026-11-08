; ---------------------------------------------------------------------------
;  WinUsage - installer (NSIS 3, Modern UI 2)
;  Installazione per-utente: nessun privilegio di amministratore.
;  Compilazione: vedi build-installer.bat / build-installer.sh
; ---------------------------------------------------------------------------
Unicode true

!define APPNAME   "WinUsage"
!define VERSION   "1.1.0"
!define EXENAME   "WinUsage.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUsage"
!define RUNKEY    "Software\Microsoft\Windows\CurrentVersion\Run"

Name "${APPNAME} ${VERSION}"
OutFile "..\dist\WinUsage-Setup-${VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\WinUsage"
InstallDirRegKey HKCU "Software\WinUsage" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"
ShowInstDetails nevershow
ShowUninstDetails nevershow

!include "MUI2.nsh"
!include "LogicLib.nsh"

; --- aspetto -------------------------------------------------------------
!define MUI_ICON   "..\app.ico"
!define MUI_UNICON "..\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEFINISHPAGE_BITMAP   "welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP   "header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "header.bmp"

!define MUI_WELCOMEPAGE_TITLE "Benvenuto in ${APPNAME}"
!define MUI_WELCOMEPAGE_TEXT "Questa procedura installa ${APPNAME} ${VERSION}, che vive nella tray di Windows e misura da quanto tempo stai usando il computer (standby escluso).$\r$\n$\r$\nL'installazione e' solo per il tuo utente e non richiede privilegi di amministratore. Ogni utente di Windows ha il proprio storico e la propria configurazione.$\r$\n$\r$\nFai clic su Avanti per continuare."

!define MUI_FINISHPAGE_TITLE "${APPNAME} e' pronto"
!define MUI_FINISHPAGE_TEXT "Installazione completata.$\r$\n$\r$\nCerca l'elmo da palombaro nella tray (vicino all'orologio): clic sinistro per il tempo di utilizzo, clic destro per storico, configurazione e uscita."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Avvia ${APPNAME} adesso"

!define MUI_UNCONFIRMPAGE_TEXT_TOP "${APPNAME} verra' rimosso da questo computer per il tuo utente. Se e' in esecuzione verra' chiuso."

; --- pagine --------------------------------------------------------------
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "Italian"

; --- informazioni sul file -----------------------------------------------
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=${LANG_ITALIAN} "ProductName"     "${APPNAME}"
VIAddVersionKey /LANG=${LANG_ITALIAN} "FileDescription" "Installer di ${APPNAME}"
VIAddVersionKey /LANG=${LANG_ITALIAN} "FileVersion"     "${VERSION}"
VIAddVersionKey /LANG=${LANG_ITALIAN} "ProductVersion"  "${VERSION}"
VIAddVersionKey /LANG=${LANG_ITALIAN} "LegalCopyright"  "${APPNAME}"

; --- chiusura dell'app in esecuzione --------------------------------------
; Prima chiede all'app di chiudersi da sola (salva i dati), poi, se serve, la termina.
!macro StopApp
  IfFileExists "$INSTDIR\${EXENAME}" 0 +3
    ExecWait '"$INSTDIR\${EXENAME}" --quit'
    Sleep 1500
  nsExec::Exec 'taskkill /F /IM ${EXENAME}'
  Pop $0
  Sleep 300
!macroend

Function .onInit
  ; In modalita' silenziosa (/S) non servono domande.
FunctionEnd

Section "WinUsage" SecMain
  SectionIn RO
  !insertmacro StopApp

  SetOutPath "$INSTDIR"
  File "..\bin\${EXENAME}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; collegamento nel menu Start (dell'utente)
  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0

  ; registrazione in "App installate" (solo per l'utente corrente)
  WriteRegStr HKCU "Software\WinUsage" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName"     "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher"       "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${EXENAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  SectionGetSize ${SecMain} $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" $0

  ; In un aggiornamento mantiene la scelta dell'utente: l'avvio automatico lo gestisce l'app
  ; (attivo di default al primo avvio, modificabile in Configurazione).
SectionEnd

Section "Uninstall"
  !insertmacro StopApp

  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"

  ; avvio automatico dell'utente corrente
  DeleteRegValue HKCU "${RUNKEY}" "WinUsage"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\WinUsage"

  ; storico e configurazione dell'utente: li elimina solo se richiesto
  IfFileExists "$LOCALAPPDATA\WinUsage\*.*" 0 done
    MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 \
      "Vuoi eliminare anche lo storico e la configurazione di questo utente?$\r$\n($LOCALAPPDATA\WinUsage)" \
      /SD IDNO IDNO done
    RMDir /r "$LOCALAPPDATA\WinUsage"
  done:
SectionEnd
