; ExplorerDock 安装包脚本（Inno Setup 6）
; 编译：ISCC.exe installer\ExplorerDock.iss
; 需要先执行：dotnet publish ExplorerDock.csproj -c Release -r win-x64 --self-contained true
;            -p:PublishSingleFile=false -o artifacts\publish-selfcontained

#define MyAppName "ExplorerDock"
#define MyAppVersion "1.0.9"
#define MyAppPublisher "v4806"
#define MyAppURL "https://github.com/v4806/ExplorerDock"
#define MyAppExeName "ExplorerDock.exe"
#define SourceDir "..\artifacts\publish-selfcontained"

[Setup]
AppId={{7C4E1B92-5F3D-4A88-9C21-6D0E5A7B4F31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=ExplorerDock-Setup-{#MyAppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; 装到用户目录，不需要管理员权限
PrivilegesRequired=lowest

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先让程序走正常退出流程，把文件夹按钮还给任务栏
Filename: "{app}\{#MyAppExeName}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitBeforeUninstall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"





