; eslee Tray Folder installer script (Inno Setup 6).
; Build:
;   ISCC /DAppVersion=0.1.1 /DSourceDir=..\artifacts\publish installer\eslee-tray-folder.iss
;
; User data (config, logs) lives under %LOCALAPPDATA%\eslee-tray-folder and is
; never touched by the uninstaller, so settings survive reinstalls.

#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppDisplayName "eslee Tray Folder"
#define MainExeName "Eslee.TrayFolder.exe"

[Setup]
AppId={{B4E7A9D2-51C8-4F63-8E2A-0D7C93415F6B}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} v{#AppVersion}
AppPublisher=eslee
DefaultDirName={autopf}\eslee Tray Folder
DefaultGroupName=eslee Tray Folder
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MainExeName}
UninstallDisplayName={#AppDisplayName}
OutputDir={#OutputDir}
OutputBaseFilename=eslee-tray-folder-setup-v{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
Compression=lzma2
SolidCompression=yes

[Languages]
#if FileExists(CompilerPath + "\Languages\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#elif FileExists(CompilerPath + "\Languages\Unofficial\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Unofficial\Korean.isl"
#else
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

[CustomMessages]
LaunchApp=eslee Tray Folder 실행
DesktopIconTask=바탕화면 바로가기 만들기
AutoStartTask=Windows 로그인 시 자동 실행 (트레이로 시작)

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStartTask}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\eslee Tray Folder"; Filename: "{app}\{#MainExeName}"
Name: "{autodesktop}\eslee Tray Folder"; Filename: "{app}\{#MainExeName}"; Tasks: desktopicon

[Registry]
; Tray Folder is a tray-resident host, so login auto-start is the default task.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "eslee-tray-folder"; ValueData: """{app}\{#MainExeName}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MainExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser
