; ExplorerDock 安装包脚本（Inno Setup 6）
; 编译：ISCC.exe installer\ExplorerDock.iss
; 需要先执行：dotnet publish ExplorerDock.csproj -c Release -r win-x64 --self-contained true
;            -p:PublishSingleFile=false -o artifacts\publish-selfcontained

#define MyAppName "ExplorerDock"
#define MyAppId "{{7C4E1B92-5F3D-4A88-9C21-6D0E5A7B4F31}"
#define MyAppVersion "1.0.15"
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
//
// 但 --quit 是靠**命名事件**通知运行中实例的：那个实例若是以管理员身份启动的
// （菜单里勾了「以管理员身份运行」），安装器（PrivilegesRequired=lowest）发不进信号，
// 文件仍被占用 —— 用户看到的就是解压阶段那个「DeleteFile 失败；错误代码 5」。
// 所以下面补一个"进程是不是真的走了"的检查：没走就当面说清楚，让人自己退出。
function ExplorerDockRunning(): Boolean;
var
  ResultCode: Integer;
  TempFile: string;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\ExplorerDock-tasklist.txt');

  if not Exec(ExpandConstant('{cmd}'),
              '/c tasklist /FI "IMAGENAME eq {#MyAppExeName}" /NH > "' + TempFile + '"',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;

  if not LoadStringsFromFile(TempFile, Lines) then
    Exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos(LowerCase('{#MyAppExeName}'), LowerCase(Lines[I])) > 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function B2S(B: Boolean): String;
begin
  if B then Result := 'true' else Result := 'false';
end;

// 诊断用：把自启/退出这摊事的每一步写进 %TEMP%\ExplorerDock-installer.log。
// 用户那边"安装时提示程序还在跑"这类问题，没有这份日志就只能靠猜。
procedure TraceInstaller(Text: string);
var
  LogFile: string;
begin
  LogFile := GetEnv('TEMP') + '\ExplorerDock-installer.log';
  SaveStringToFile(LogFile, GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + ' ' + Text + #13#10, True);
end;

// 让正在运行的实例退出，并**等它真的退干净**。
// 只发一次 --quit 会踩坑：界面进程退出后 --host 进程还要一两秒才结束，
// 立刻检查就会以为"还在跑"，安装程序反倒把自己拦下来（静默安装直接 exit 1）。
function StopExplorerDock(ExePath: string): Boolean;
var
  ResultCode: Integer;
  Waited: Integer;
begin
  Waited := 0;
  TraceInstaller('stop: exe=[' + ExePath + '] exists=' + B2S(FileExists(ExePath)));

  while Waited < 12000 do
  begin
    if not ExplorerDockRunning() then
    begin
      Result := True;
      TraceInstaller('stop: 没有进程在跑了（等了 ' + IntToStr(Waited) + 'ms）');
      Exit;
    end;

    if FileExists(ExePath) then
    begin
      if Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        TraceInstaller('stop: 已发 --quit rc=' + IntToStr(ResultCode) + '（第 ' + IntToStr(Waited div 1000 + 1) + ' 次）')
      else
        TraceInstaller('stop: 发 --quit 失败（Exec 返回 false）');
    end
    else
      TraceInstaller('stop: exe 不存在，发不出 --quit');

    Sleep(1000);
    Waited := Waited + 1000;
  end;

  Result := not ExplorerDockRunning();
  TraceInstaller('stop: 超时，仍在运行=' + B2S(not Result));
end;

procedure ExplainStillRunning();
begin
  MsgBox('ExplorerDock 还在运行，安装程序没能让它自动退出。' + #13#10 + #13#10 +
         '它可能是以管理员身份启动的（这种实例收不到安装程序的退出通知）。' + #13#10 +
         '请右键任务栏右下角的 ExplorerDock 图标，选择「退出 ExplorerDock」，' + #13#10 +
         '然后再运行一次安装程序。',
         mbError, MB_OK);
end;

function InitializeSetup(): Boolean;
var
  InstallDir: string;
  ExePath: string;
begin
  Result := True;
  ExePath := '';

  // 注意键名是**单个**大括号。
  //
  // [Setup] 里的 AppId={#MyAppId}，而 MyAppId 定义成了 "{{7C4E1B92-...}}"（双括号是 Inno 的转义，
  // 落盘后键名只有一个 {）。可 [Code] 里的字符串**没有转义语义**，照抄 {#MyAppId} 会得到两个 {，
  // 于是永远读不到 —— 2026-09-20 实测就是这样：installLocation 为空 → 退出指令从没发出去 →
  // 安装程序只会反复检测到"程序还在跑"，最后把用户拦在门口。
  if RegQueryStringValue(HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C4E1B92-5F3D-4A88-9C21-6D0E5A7B4F31}_is1',
       'InstallLocation', InstallDir) then
    ExePath := AddBackslash(InstallDir) + '{#MyAppExeName}';

  // 还没走干净：宁可在这里中止并说清楚，也不要等到解压阶段弹「重试/跳过/取消」
  TraceInstaller('setup: installLocation=[' + ExePath + ']');
  if not StopExplorerDock(ExePath) then
  begin
    ExplainStillRunning();
    Result := False;
  end;
end;

// 再兜一层：真正开始覆盖文件之前确认旧实例已经走了（万一上面那次没赶上）
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  if not StopExplorerDock(ExpandConstant('{app}\{#MyAppExeName}')) then
    Result := 'ExplorerDock 仍在运行（可能是以管理员身份启动的，收不到退出通知）。' + #13#10 +
              '请右键托盘图标选择「退出 ExplorerDock」，然后重试。';
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




























































