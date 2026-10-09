; Installer di activity-tracker per Windows (NSIS 3, Modern UI 2).
;
; Si installa per l'utente corrente, senza diritti di amministratore, in %LOCALAPPDATA%\Programs\activity-tracker:
; nessun avviso UAC, e l'agente gira come l'utente che misura. Lo compila scripts/build.sh (o build.ps1):
;   makensis -DVERSION=0.3.0 -DSOURCE_DIR=<cartella col .exe pubblicato> -DOUT_FILE=<dist\e-track-agent-windows-setup.exe> activity-tracker.nsi
;
; Non firmato (come l'app Mac firmata ad-hoc): al primo avvio SmartScreen mostra «Windows ha protetto il PC»,
; si supera con «Ulteriori informazioni» → «Esegui comunque» (vedi LEGGIMI.txt).

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!ifndef VERSION
  !define VERSION "0.3.0"
!endif
!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\build\publish"
!endif
!ifndef OUT_FILE
  !define OUT_FILE "..\dist\e-track-agent-windows-setup.exe"
!endif

!define APP_NAME "activity-tracker"
!define APP_EXE "activity-tracker.exe"
!define PUBLISHER "E-quipe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"

Name "${APP_NAME}"
OutFile "${OUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
ShowInstDetails nevershow
ShowUninstDetails nevershow
BrandingText "${APP_NAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1040 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1040 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1040 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1040 "FileDescription" "Installazione di ${APP_NAME}"
VIAddVersionKey /LANG=1040 "CompanyName" "${PUBLISHER}"
VIAddVersionKey /LANG=1040 "LegalCopyright" "${PUBLISHER}"

!include "MUI2.nsh"
!include "LogicLib.nsh"

!define MUI_ICON "..\assets\activity-tracker.ico"
!define MUI_UNICON "..\assets\activity-tracker.ico"
!define MUI_ABORTWARNING

!define MUI_WELCOMEPAGE_TITLE "Installazione di ${APP_NAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "${APP_NAME} misura come usi il PC (app e siti in primo piano, inattività, video) e lo invia a equipe-track solo mentre hai una sessione di lavoro aperta.$\r$\n$\r$\nSi installa solo per il tuo utente e parte da sola all'accesso a Windows, come icona nella barra delle applicazioni, vicino all'orologio.$\r$\n$\r$\nPremi Avanti per continuare."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "${APP_NAME} è installato"
!define MUI_FINISHPAGE_TEXT "L'icona compare nella barra delle applicazioni, vicino all'orologio (se non la vedi, apri la freccia ^ delle icone nascoste).$\r$\n$\r$\nAl primo avvio si aprono le Impostazioni: inserisci l'indirizzo del server e il token che ti ha dato chi gestisce equipe-track."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Avvia ${APP_NAME} adesso"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\LEGGIMI.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Apri LEGGIMI"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Italian"

; Chiude con garbo la copia in esecuzione (salva gli ultimi secondi e fa l'ultimo invio), poi, se serve, la ferma.
!macro StopRunningAgent
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
    ExecWait '"$INSTDIR\${APP_EXE}" --quit'
  ${EndIf}
  nsExec::Exec 'taskkill /IM "${APP_EXE}" /F'
  Pop $0
  Sleep 500
!macroend

Section "Installa"
  SetOutPath "$INSTDIR"
  !insertmacro StopRunningAgent

  File "${SOURCE_DIR}\${APP_EXE}"
  File "/oname=LEGGIMI.txt" "LEGGIMI.txt"
  WriteUninstaller "$INSTDIR\Disinstalla.exe"

  CreateShortcut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  ; Avvio all'accesso a Windows (per utente). L'app lo gestisce anche dalle Impostazioni.
  WriteRegStr HKCU "${RUN_KEY}" "${APP_NAME}" '"$INSTDIR\${APP_EXE}" --autostart'

  ; Voce in Impostazioni › App › App installate.
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Disinstalla.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Disinstalla.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  SectionGetSize 0 $0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
SectionEnd

Section "Uninstall"
  !insertmacro StopRunningAgent

  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Eliminare anche i dati misurati su questo PC e il collegamento a equipe-track (server, token, segreto)?$\r$\n$\r$\nI dati già inviati a equipe-track restano su equipe-track." /SD IDNO IDNO keep_data
    ExecWait '"$INSTDIR\${APP_EXE}" --forget-credentials'
    RMDir /r "$LOCALAPPDATA\${APP_NAME}"
  keep_data:

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\LEGGIMI.txt"
  Delete "$INSTDIR\Disinstalla.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  DeleteRegValue HKCU "${RUN_KEY}" "${APP_NAME}"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" "${APP_NAME}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
