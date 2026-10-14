; FolderSync - Inno Setup 6 script
; Cach dung: chay packaging\build-release.ps1 truoc, roi mo file nay bang Inno Setup va Compile.
; Bo cai KHONG kem portable.flag -> state nam o %AppData%\FolderSync\state.db (dung cho ban cai dat).

#define MyAppName "FolderSync"
#define MyAppVersion "0.1.0"
#define MyAppExeName "FolderSync.App.exe"

[Setup]
AppId={{8F2A0C41-3E0D-4B7A-9A51-FOLDERSYNC01}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\FolderSync
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=FolderSync-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\dist\installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Tao shortcut tren Desktop"; GroupDescription: "Tuy chon:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Mo FolderSync ngay"; Flags: nowait postinstall skipifsilent
