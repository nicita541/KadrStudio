#define MyAppName "Kadr Studio"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Kadr Studio"
#define MyAppExeName "KadrStudio.exe"

[Setup]
AppId={{98C4FE3D-BCBC-48C4-9C42-4040379ED6E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Kadr Studio
DefaultGroupName=Kadr Studio
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=KadrStudio-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\release\KadrStudio-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Kadr Studio"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Kadr Studio"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Kadr Studio"; Flags: nowait postinstall skipifsilent

