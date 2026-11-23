; ==============================================================================
; Local Model Conductor (本地模型调度台) - Official NSIS Installer Script
; Architecture: Windows x64 (win-x64), .NET 10 Self-Contained
; Security: RequestExecutionLevel user (Per-User, Zero Admin Rights Required)
; Standards: Dual-Language (SimpChinese, English), Safe Upgrade, Data Retention
; ==============================================================================

Unicode true
RequestExecutionLevel user

!define PRODUCT_NAME "Local Model Conductor"
!define PRODUCT_NAME_ZH "本地模型调度台"
!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "3.2.0"
!endif
!define PRODUCT_PUBLISHER "oliverhe202018-ctrl"
!define PRODUCT_WEB_SITE "https://github.com/oliverhe202018-ctrl/Local-Model-Conductor"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalModelConductor"
!define PRODUCT_REG_KEY "Software\LocalModelConductor"

!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\dist\LlamaCppWindowsManager-win-x64"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "..\dist\release"
!endif
!ifndef APP_ICON
  !define APP_ICON "..\src\LocalLlmConsole.App\Assets\AppIcon.ico"
!endif

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTPUT_DIR}\LocalModelConductor-Setup-win-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\LocalModelConductor"
InstallDirRegKey HKCU "${PRODUCT_REG_KEY}" "InstallDir"

VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME_ZH}"
VIAddVersionKey /LANG=2052 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright (C) 2026 ${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME_ZH} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductVersion" "${PRODUCT_VERSION}.0"

VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (C) 2026 ${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}.0"

SetCompressor /SOLID lzma

; ----------------- Modern UI Configuration -----------------
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"

; Installer Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\LlamaCppWindowsManager.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动本地模型调度台 (Launch Local Model Conductor)"
!insertmacro MUI_PAGE_FINISH

; Uninstaller Pages
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; Languages
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ----------------- Installation Section -----------------
Section "MainSection" SEC01
  SetOutPath "$INSTDIR"
  SetOverwrite on

  ; Ensure 64-bit target
  ${If} ${RunningX64}
    DetailPrint "Detected 64-bit Windows OS. Installing x64 Native Binaries..."
  ${Else}
    MessageBox MB_OK|MB_ICONSTOP "Local Model Conductor requires 64-bit Windows."
    Abort
  ${EndIf}

  ; Check and close running instances gracefully before file overwrite
  DetailPrint "Checking running Guard and Console processes..."
  nsExec::Exec 'taskkill /F /IM "LocalLLMManager.Guard.Service.exe" /FI "STATUS eq RUNNING"'
  nsExec::Exec 'taskkill /F /IM "LlamaCppWindowsManager.exe" /FI "STATUS eq RUNNING"'
  Sleep 500

  ; Copy Core Binaries
  File "${SOURCE_DIR}\LlamaCppWindowsManager.exe"
  File "${SOURCE_DIR}\LlamaCppWindowsManager.exe.sha256"
  File "${SOURCE_DIR}\llwmctl.exe"
  File "${SOURCE_DIR}\llwmctl.exe.sha256"

  ; Copy Guard Safety Service
  CreateDirectory "$INSTDIR\Guard"
  SetOutPath "$INSTDIR\Guard"
  File "${SOURCE_DIR}\Guard\LocalLLMManager.Guard.Service.exe"
  File "${SOURCE_DIR}\Guard\LocalLLMManager.Guard.Service.exe.sha256"
  File /nonfatal "${SOURCE_DIR}\Guard\*.json"

  ; Copy Documentation and Licenses
  SetOutPath "$INSTDIR"
  File "${SOURCE_DIR}\LICENSE"
  File "${SOURCE_DIR}\THIRD-PARTY-NOTICES.md"
  File "${SOURCE_DIR}\agent.md"
  File "${SOURCE_DIR}\AGENTS.md"
  File "${SOURCE_DIR}\sbom.spdx.json"

  CreateDirectory "$INSTDIR\docs"
  SetOutPath "$INSTDIR\docs"
  File "${SOURCE_DIR}\docs\CONTROL_API.md"

  CreateDirectory "$INSTDIR\licenses"
  SetOutPath "$INSTDIR\licenses"
  File /r "${SOURCE_DIR}\licenses\*.*"

  ; Ensure Data Directory Structure exists (NEVER OVERWRITTEN)
  CreateDirectory "$INSTDIR\data"
  CreateDirectory "$INSTDIR\data\models"
  CreateDirectory "$INSTDIR\data\runtimes"
  CreateDirectory "$INSTDIR\data\state"
  CreateDirectory "$INSTDIR\data\logs"

  ; Write Uninstaller
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Shortcuts
  CreateDirectory "$SMPROGRAMS\Local Model Conductor"
  CreateShortcut "$SMPROGRAMS\Local Model Conductor\Local Model Conductor.lnk" "$INSTDIR\LlamaCppWindowsManager.exe" "" "$INSTDIR\LlamaCppWindowsManager.exe" 0
  CreateShortcut "$SMPROGRAMS\Local Model Conductor\llwmctl CLI Shell.lnk" "cmd.exe" '/k "cd /d $INSTDIR && llwmctl.exe status"' "" 0
  CreateShortcut "$SMPROGRAMS\Local Model Conductor\卸载本地模型调度台.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  CreateShortcut "$DESKTOP\Local Model Conductor.lnk" "$INSTDIR\LlamaCppWindowsManager.exe" "" "$INSTDIR\LlamaCppWindowsManager.exe" 0

  ; Registry Registrations (Per-User HKCU)
  WriteRegStr HKCU "${PRODUCT_REG_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${PRODUCT_REG_KEY}" "Version" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayName" "${PRODUCT_NAME_ZH} (${PRODUCT_NAME})"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\LlamaCppWindowsManager.exe"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "URLInfoAbout" "${PRODUCT_WEB_SITE}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
SectionEnd

; ----------------- Uninstallation Section -----------------
Section "Uninstall"
  ; Stop running instances
  DetailPrint "Terminating Local Model Conductor processes..."
  nsExec::Exec 'taskkill /F /IM "LocalLLMManager.Guard.Service.exe" /FI "STATUS eq RUNNING"'
  nsExec::Exec 'taskkill /F /IM "LlamaCppWindowsManager.exe" /FI "STATUS eq RUNNING"'
  Sleep 500

  ; Ask user whether to delete or keep user data
  ; Default: Keep data (MessageBox MB_YESNO | MB_DEFBUTTON2)
  ; In silent mode, default strictly to KeepData without prompting!
  IfSilent KeepData
  MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION \
    "是否同时删除所有本地模型、配置文件与数据库 (位于 $INSTDIR\data)？$\r$\n$\r$\n[是] 彻底清空全部数据$\r$\n[否 (推荐)] 保留模型与配置" \
    IDYES DeleteData IDNO KeepData

DeleteData:
  DetailPrint "Purging user data folder: $INSTDIR\data..."
  RMDir /r "$INSTDIR\data"
  Goto RemoveBinaries

KeepData:
  DetailPrint "Preserving user models and database in $INSTDIR\data."

RemoveBinaries:
  ; Remove shortcuts
  Delete "$DESKTOP\Local Model Conductor.lnk"
  Delete "$SMPROGRAMS\Local Model Conductor\Local Model Conductor.lnk"
  Delete "$SMPROGRAMS\Local Model Conductor\llwmctl CLI Shell.lnk"
  Delete "$SMPROGRAMS\Local Model Conductor\卸载本地模型调度台.lnk"
  RMDir "$SMPROGRAMS\Local Model Conductor"

  ; Remove Program Files
  Delete "$INSTDIR\LlamaCppWindowsManager.exe"
  Delete "$INSTDIR\LlamaCppWindowsManager.exe.sha256"
  Delete "$INSTDIR\llwmctl.exe"
  Delete "$INSTDIR\llwmctl.exe.sha256"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.md"
  Delete "$INSTDIR\agent.md"
  Delete "$INSTDIR\AGENTS.md"
  Delete "$INSTDIR\sbom.spdx.json"
  Delete "$INSTDIR\Uninstall.exe"

  RMDir /r "$INSTDIR\Guard"
  RMDir /r "$INSTDIR\docs"
  RMDir /r "$INSTDIR\licenses"

  ; Remove install dir if empty
  RMDir "$INSTDIR"

  ; Clean registry keys
  DeleteRegKey HKCU "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKCU "${PRODUCT_REG_KEY}"
SectionEnd
