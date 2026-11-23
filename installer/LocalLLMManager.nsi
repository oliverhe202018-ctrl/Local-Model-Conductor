; ==============================================================================
; Local LLM Manager (本地大模型管理器) - Official NSIS Installer Script
; Architecture: Windows x64 (win-x64)
; Security: RequestExecutionLevel user (Per-User, Zero Admin Rights Required)
; Standards: Dual-Language (SimpChinese, English), Safe Upgrade, Non-Destructive
; ==============================================================================

Unicode true
RequestExecutionLevel user

!define PRODUCT_NAME "Local LLM Manager"
!define PRODUCT_NAME_ZH "本地模型管理器"
!define PRODUCT_VERSION "3.2.0"
!define PRODUCT_PUBLISHER "oliverhe202018-ctrl"
!define PRODUCT_WEB_SITE "https://github.com/oliverhe202018-ctrl/Local-Model-Conductor"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalLLMManager"
!define PRODUCT_REG_KEY "Software\LocalLLMManager"

!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\release\portable\LocalLLMManager-Portable-win-x64"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "..\release\installer"
!endif

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTPUT_DIR}\LocalLLMManager-Setup-v3.2.0-win-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\LocalLLMManager"
InstallDirRegKey HKCU "${PRODUCT_REG_KEY}" "InstallDir"

VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME_ZH}"
VIAddVersionKey /LANG=2052 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright (C) 2026 ${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME_ZH} 安装程序 (Community Unsigned)"
VIAddVersionKey /LANG=2052 "FileVersion" "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductVersion" "${PRODUCT_VERSION}.0"

VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (C) 2026 ${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Installer (Community Unsigned)"
VIAddVersionKey /LANG=1033 "FileVersion" "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}.0"

SetCompressor /SOLID lzma

; ----------------- Modern UI Configuration -----------------
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "D:\project\本地模型计算\phase4b_windows_manager_fork\repo\src\LocalLlmConsole.App\Assets\AppIcon.ico"
!define MUI_UNICON "D:\project\本地模型计算\phase4b_windows_manager_fork\repo\src\LocalLlmConsole.App\Assets\AppIcon.ico"

; Installer Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\LlamaCppWindowsManager.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动本地大模型管理器 (Launch Local LLM Manager)"
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
    MessageBox MB_OK|MB_ICONSTOP "Local LLM Manager requires 64-bit Windows."
    Abort
  ${EndIf}

  ; Non-Destructive check: only close local GUI if open, NEVER kill background models or Guard!
  DetailPrint "Checking existing local UI process..."
  nsExec::Exec 'cmd.exe /c taskkill /IM "LlamaCppWindowsManager.exe" 2>nul'
  Sleep 300

  ; Copy Core Binaries & Libraries from Portable staging
  File /r "${SOURCE_DIR}\*.*"

  ; Ensure Data & Log Directories exist (NEVER OVERWRITTEN)
  CreateDirectory "$INSTDIR\data"
  CreateDirectory "$INSTDIR\data\models"
  CreateDirectory "$INSTDIR\data\state"
  CreateDirectory "$INSTDIR\data\logs"

  ; Write Uninstaller
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Shortcuts
  CreateDirectory "$SMPROGRAMS\Local LLM Manager"
  CreateShortcut "$SMPROGRAMS\Local LLM Manager\Local LLM Manager.lnk" "$INSTDIR\LlamaCppWindowsManager.exe" "" "$INSTDIR\LlamaCppWindowsManager.exe" 0
  CreateShortcut "$SMPROGRAMS\Local LLM Manager\llwmctl CLI Shell.lnk" "cmd.exe" '/k "cd /d $INSTDIR && llwmctl.exe status"' "" 0
  CreateShortcut "$SMPROGRAMS\Local LLM Manager\卸载本地模型管理器.lnk" "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0
  CreateShortcut "$DESKTOP\Local LLM Manager.lnk" "$INSTDIR\LlamaCppWindowsManager.exe" "" "$INSTDIR\LlamaCppWindowsManager.exe" 0

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
  DetailPrint "Closing local UI if open..."
  nsExec::Exec 'cmd.exe /c taskkill /IM "LlamaCppWindowsManager.exe" 2>nul'
  Sleep 300

  ; Ask user whether to delete or keep user data
  ; Default: Keep data (MessageBox MB_YESNO | MB_DEFBUTTON2)
  ; In silent mode, default strictly to KeepData without prompting!
  IfSilent KeepData
  MessageBox MB_YESNO|MB_DEFBUTTON2|MB_ICONQUESTION \
    "是否同时删除所有本地模型、配置文件与数据库 (位于 $INSTDIR\data)？$\r$\n$\r$\n[是] 彻底清空全部数据$\r$\n[否 (推荐)] 保留模型与配置" \
    IDYES DeleteData IDNO KeepData

DeleteData:
  DetailPrint "Purging local data folder: $INSTDIR\data..."
  RMDir /r "$INSTDIR\data"
  Goto RemoveBinaries

KeepData:
  DetailPrint "Preserving user models and database in $INSTDIR\data."

RemoveBinaries:
  ; Remove shortcuts
  Delete "$DESKTOP\Local LLM Manager.lnk"
  Delete "$SMPROGRAMS\Local LLM Manager\Local LLM Manager.lnk"
  Delete "$SMPROGRAMS\Local LLM Manager\llwmctl CLI Shell.lnk"
  Delete "$SMPROGRAMS\Local LLM Manager\卸载本地模型管理器.lnk"
  RMDir "$SMPROGRAMS\Local LLM Manager"

  ; Remove Program Binaries and Subfolders
  Delete "$INSTDIR\LlamaCppWindowsManager.exe"
  Delete "$INSTDIR\llwmctl.exe"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.md"
  Delete "$INSTDIR\sbom.spdx.json"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"

  RMDir /r "$INSTDIR\bin"
  RMDir /r "$INSTDIR\Guard"
  RMDir /r "$INSTDIR\templates"
  RMDir /r "$INSTDIR\scripts"
  RMDir /r "$INSTDIR\docs"
  RMDir /r "$INSTDIR\licenses"

  ; Remove install dir if empty
  RMDir "$INSTDIR"

  ; Clean registry keys
  DeleteRegKey HKCU "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKCU "${PRODUCT_REG_KEY}"
SectionEnd
