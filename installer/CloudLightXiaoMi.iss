#define AppName "CloudLight XiaoMi"
#define AppVersion "2.1.1"
#define AppExeName "CloudLight.XiaoMi.exe"
#define PublishDir "..\artifacts\release\2.1.1"

[Setup]
AppId={{8E701BE1-0F30-4C52-89FA-AB72D2E5E126}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=CloudLight
DefaultDirName={localappdata}\Programs\CloudLight XiaoMi
DefaultGroupName=CloudLight XiaoMi
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=CloudLight-XiaoMi-Setup-2.1.1
SetupIconFile=..\src\CloudLight.Presence.App\Assets\CloudLightPresence.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=2.1.1.0
VersionInfoProductVersion=2.1.1.0
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer

[Languages]
Name: "chinesesimp"; MessagesFile: ".\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CloudLight XiaoMi"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\CloudLight XiaoMi"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 CloudLight XiaoMi"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
