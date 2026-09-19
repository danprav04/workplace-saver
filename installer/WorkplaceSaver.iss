; =====================================================================
; Workplace Saver - Inno Setup Installer Script
; Modern, sleek desktop installer with auto-run and shortcut tasks
; =====================================================================

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif

#define MyAppName "Workplace Saver"
#define MyAppPublisher "Daniel Pravutiner"
#define MyAppURL "https://github.com/danprav04/workplace-saver"
#define MyAppExeName "WorkplaceSaver.exe"

#ifndef SourceDir
#define SourceDir "..\publish"
#endif

#ifndef OutputDir
#define OutputDir "..\dist"
#endif

[Setup]
AppId={{E1D4A0B7-3475-4309-80EF-6B64883A3870}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; Modern installation mode: defaults to user profile, doesn't require admin elevation
DefaultDirName={autopf}\WorkplaceSaver
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir={#OutputDir}
OutputBaseFilename=WorkplaceSaver-Setup-v{#MyAppVersion}
SetupIconFile=..\src\WorkplaceSaver\Assets\app.ico
UninstallDisplayIcon={app}\Assets\app.ico

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=auto
CloseApplications=yes
RestartApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Automatically start Workplace Saver in background on Windows startup"; GroupDescription: "System Integration:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"; Tasks: desktopicon

[Registry]
; Auto-start on Windows boot with --startup flag
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WorkplaceSaver"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
