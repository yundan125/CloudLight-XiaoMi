#define AppName "CloudLight Presence"
#define AppVersion "1.0.0"
#define AppExeName "CloudLight.Presence.App.exe"
#define PublishDir "..\artifacts\win-x64-1.0.0"

[Setup]
AppId={{8E701BE1-0F30-4C52-89FA-AB72D2E5E126}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=CloudLight
DefaultDirName={localappdata}\Programs\CloudLight Presence
DefaultGroupName=CloudLight Presence
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=CloudLight-Presence-Setup-1.0.0
SetupIconFile=..\src\CloudLight.Presence.App\Assets\CloudLightPresence.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=1.0.0.0
VersionInfoProductVersion=1.0.0.0
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CloudLight Presence"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\CloudLight Presence"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 CloudLight Presence"; Flags: nowait postinstall skipifsilent
