; ==============================================================================
; Local LLM Manager - Production Release Inno Setup Script
; Upstream: alekk89/llama-cpp-windows-manager v3.0.0
; Integrated: LocalLLMManager.Guard + llwmctl + LlamaCppWindowsManager
; Architecture: Windows x64 (win-x64), .NET 10 Self-Contained
; Security: PrivilegesRequired=lowest (Per-User, Zero Elevation)
; ==============================================================================

#define AppName "llama.cpp Windows Manager"
#define AppExeName "LlamaCppWindowsManager.exe"
#define AppCliName "llwmctl.exe"
#define GuardExeName "LocalLLMManager.Guard.Service.exe"
#ifndef AppVersion
#define AppVersion "3.0.0"
#endif
#ifndef SourceDir
#define SourceDir "dist\LlamaCppWindowsManager-win-x64"
#endif
#ifndef OutputDir
#define OutputDir "dist\installer"
#endif

[Setup]
AppId={{5C6D440C-0EE0-4FEC-8D86-6AADEAA24620}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=llama.cpp Windows Manager & LocalLLMManager Contributors
AppPublisherURL=https://github.com/alekk89/llama-cpp-windows-manager
AppSupportURL=https://github.com/alekk89/llama-cpp-windows-manager/issues
AppUpdatesURL=https://github.com/alekk89/llama-cpp-windows-manager/releases
DefaultDirName={code:GetDefaultDirName}
DefaultGroupName={#AppName}
UsePreviousAppDir=yes
UsePreviousGroup=no
UsePreviousTasks=yes
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=LlamaCppWindowsManager-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\LocalLlmConsole.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
AppMutex=Local\llama.cpp-console-single-instance,Local\LocalLLMManager.Guard.SingleInstance
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}
VersionInfoCompany=LocalLLMManager Contributors
VersionInfoDescription={#AppName} Production Installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start with Windows"; GroupDescription: "System integration:"; Flags: unchecked

[Files]
; --- Core Application Executables ---
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\{#AppExeName}.sha256"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\{#AppCliName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\{#AppCliName}.sha256"; DestDir: "{app}"; Flags: ignoreversion

; --- Guard Safety Control Service ---
Source: "{#SourceDir}\Guard\{#GuardExeName}"; DestDir: "{app}\Guard"; Flags: ignoreversion
Source: "{#SourceDir}\Guard\{#GuardExeName}.sha256"; DestDir: "{app}\Guard"; Flags: ignoreversion
Source: "{#SourceDir}\Guard\web.config"; DestDir: "{app}\Guard"; Flags: ignoreversion
Source: "{#SourceDir}\Guard\*.json"; DestDir: "{app}\Guard"; Flags: ignoreversion skipifsourcedoesntexist

; --- Documentation & Specifications ---
Source: "{#SourceDir}\AGENTS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\agent.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\docs\CONTROL_API.md"; DestDir: "{app}\docs"; Flags: ignoreversion

; --- Legal, Licenses, and Software Bill of Materials (SPDX) ---
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\sbom.spdx.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Clean up obsolete files from legacy releases if present
Type: files; Name: "{app}\LlamaCppConsole.exe"
Type: files; Name: "{userprograms}\llama.cpp Console\llama.cpp Console.lnk"
Type: dirifempty; Name: "{userprograms}\llama.cpp Console"
Type: files; Name: "{userdesktop}\llama.cpp Console.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\llwmctl CLI Shell"; Filename: "cmd.exe"; Parameters: "/k ""cd /d {app} && llwmctl.exe status"""; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LlamaCppWindowsManager"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteAppDataOnUninstall: Boolean;

function GetDefaultDirName(Param: string): string;
begin
  if DirExists('D:\') then
    Result := 'D:\LlamaCppWindowsManager'
  else
    Result := ExpandConstant('{localappdata}\Programs\LlamaCppWindowsManager');
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  DataDir: string;
begin
  Result := True;
  DeleteAppDataOnUninstall := False;
  DataDir := ExpandConstant('{app}\data');

  if DirExists(DataDir) then
  begin
    DeleteAppDataOnUninstall :=
      SuppressibleMsgBox(
        'Uninstall ' + '{#AppName}' + '?' + #13#10 + #13#10 +
        'Your models (GGUF), runtimes, logs, and database in:' + #13#10 +
        DataDir + #13#10 + #13#10 +
        'will be KEPT by default so you do not lose downloaded models.' + #13#10 + #13#10 +
        'Do you want to permanently delete all model weights and settings too?',
        mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2,
        IDNO) = IDYES;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Clean up temporary sidecar manifest
    DeleteFile(ExpandConstant('{app}\.llwm-sidecars.json'));
    
    // Purge data folder ONLY if the user explicitly requested data destruction
    if DeleteAppDataOnUninstall then
    begin
      DelTree(ExpandConstant('{app}\data'), True, True, True);
    end;
  end;
end;
