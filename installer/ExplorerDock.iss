; ExplorerDock 安装包脚本（Inno Setup 6）
; 编译：ISCC.exe installer\ExplorerDock.iss
; 需要先执行：dotnet publish ExplorerDock.csproj -c Release -r win-x64 --self-contained true
;            -p:PublishSingleFile=false -o artifacts\publish-selfcontained

#define MyAppName "ExplorerDock"
#define MyAppId "{{7C4E1B92-5F3D-4A88-9C21-6D0E5A7B4F31}"
#define MyAppVersion "1.0.11"
#define MyAppPublisher "v4806"
#define MyAppURL "https://github.com/v4806/ExplorerDock"
#define MyAppExeName "ExplorerDock.exe"
#define SourceDir "..\artifacts\publish-selfcontained"

[Setup]
AppId={#MyAppId}
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

; 注意：这里**不能**写 AppMutex —— 它会让安装程序在向导一开始就弹
; 「检测到 ExplorerDock 正在运行，请先关闭」，而那会儿我们的自动退出还没跑。
; 自动退出统一由 [Code] 的 InitializeSetup / PrepareToInstall 负责。
CloseApplications=yes
RestartApplications=no

[Languages]
; 英文用 Inno 自带的 compiler:Default.isl（本机这份是英文）；
; 中文用随项目带的 ChineseSimplified.isl
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
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

[Code]
// 安装程序一启动就把正在运行的旧版本请走。
//
// 为什么放在这里而不是 PrepareToInstall：用户看到"准备安装"之前就已经开始复制文件了，
// 而且最早的时机处理掉，才不会走到"检测到程序正在运行"那种把人卡住的提示。
// --quit 会让它走正常退出流程（窗口还给任务栏、无损取消接管），比直接 taskkill 干净。
function InitializeSetup(): Boolean;
var
  InstallDir: string;
  ExePath: string;
  ResultCode: Integer;
begin
  Result := True;

  if RegQueryStringValue(HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1',
       'InstallLocation', InstallDir) then
  begin
    ExePath := AddBackslash(InstallDir) + '{#MyAppExeName}';

    if FileExists(ExePath) then
    begin
      Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(2000);
    end;
  end;
end;

// 再兜一层：真正开始覆盖文件之前确认旧实例已经走了（万一上面那次没赶上）
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
end;

// 卸载时问一句要不要连设置和剪贴板数据一起删掉。
// 默认按钮是"否"（MB_DEFBUTTON2）—— 也就是默认保留数据。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  TempDir: string;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  // 静默卸载不问，保持数据
  if UninstallSilent then
    Exit;

  DataDir := ExpandConstant('{userappdata}\ExplorerDock');

  if DirExists(DataDir) then
  begin
    if MsgBox('是否同时删除 ExplorerDock 的设置与剪贴板数据？' + #13#10 + #13#10 +
              '数据目录：' + DataDir + #13#10 + #13#10 +
              '选"否"会保留这些数据，重新安装后仍然可用。',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataDir, True, True, True);
  end;

  // 顺手清掉临时目录里的日志与拖放中转文件
  TempDir := GetEnv('TEMP');

  if TempDir <> '' then
  begin
    DelTree(TempDir + '\ExplorerDock', True, True, True);
    DeleteFile(TempDir + '\ExplorerDock.dock.log');
    DeleteFile(TempDir + '\ExplorerDock.clipboard.log');
    DeleteFile(TempDir + '\ExplorerDock.alttab.log');
    DeleteFile(TempDir + '\ExplorerDock.aumid.log');
  end;
end;




























































