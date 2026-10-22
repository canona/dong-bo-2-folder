; FolderSync - Inno Setup 6 script
; Cach dung: chay packaging\build-release.ps1 (tu publish + tu bien dich file nay neu tim thay ISCC.exe).
; Bo cai KHONG kem portable.flag -> state nam o %AppData%\FolderSync\state.db (du lieu nguoi dung,
; GIU LAI khi go cai dat de cai lai khong mat Job).
; Ban publish la self-contained single-file: da kem .NET runtime + thu vien SQLite native -> may dich
; KHONG can cai .NET.

#define MyAppName "FolderSync"
; Phien ban + ban quyen: giu KHOP voi Directory.Build.props (Version, Company, Copyright)
#define MyAppVersion "0.3.1"
#define MyAppPublisher "VTC Digital"
#define MyAppCopyright "Copyright (C) 2026 VTC Digital. All rights reserved."
#define MyAppExeName "FolderSync.App.exe"
; Trung voi App.MutexName (src\FolderSync.App\App.xaml.cs): bo cai/go cai doi app dong truoc
#define MyAppMutex "FolderSyncSingleInstance"

[Setup]
AppId={{8F2A0C41-3E0D-4B7A-9A51-FOLDERSYNC01}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppCopyright}
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright={#MyAppCopyright}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Bo cai {#MyAppName} - phat trien thang 10/2026 boi Canona
; Cai cho user hien tai, KHONG can quyen admin (vao %LocalAppData%\Programs\FolderSync);
; nguoi dung van co the chon cai cho moi user (can admin) o hop thoai dau.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\FolderSync
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AppMutex={#MyAppMutex}
CloseApplications=yes
SetupIconFile=..\src\FolderSync.App\Assets\foldersync.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=FolderSync-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Tasks]
Name: "desktopicon"; Description: "Tao shortcut tren Desktop"; GroupDescription: "Tuy chon:"
Name: "autostart"; Description: "Chay FolderSync cung Windows (an o khay he thong, cac Job tu dong van chay)"; GroupDescription: "Tuy chon:"

[Files]
Source: "..\dist\installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} (Mini)"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--mini"
Name: "{group}\Go cai dat {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Chay cung Windows: CUNG ten + CUNG tham so voi StartupManager trong app (bat/tat o 2 noi la mot)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FolderSync"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart
; Go cai dat luon xoa gia tri tu khoi dong (ke ca khi nguoi dung bat tu trong app, khong qua bo cai)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "FolderSync"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Mo FolderSync ngay"; Flags: nowait postinstall skipifsilent
