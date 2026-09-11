#define AppName "PomoDock"
#define AppPublisher "TORO404DEV"
#define AppExeName "PomoDock.exe"
#define AppVersion GetEnv("POMODOCK_VERSION")
#if AppVersion == ""
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{55AD6F88-FE44-4EDF-8A57-B0B7A68E7D4C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/TORO404DEV/focus-dock
AppSupportURL=https://github.com/TORO404DEV/focus-dock/issues
AppUpdatesURL=https://github.com/TORO404DEV/focus-dock/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=PomoDock-Setup-{#AppVersion}
SetupIconFile=..\src\PomoDock.App\Assets\PomoDock.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=PomoDock installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PomoDock"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\PomoDock"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch PomoDock"; Flags: nowait postinstall skipifsilent
