; ============================================================================================
;  FluentFlyout Inno Setup 安装脚本 —— ARM64 自包含版
; --------------------------------------------------------------------------------------------
;  编译方式（Inno Setup 6.x）：
;      "D:\Inno Setup 6.6.1\ISCC.exe" installer\FluentFlyout-arm64.iss
;  或直接双击 / 用 Compil32.exe 打开本文件后点“编译”。
;
;  编译之前请先构建程序本体（脚本只打包 bin\ARM64\Release-SelfContained 的产物，不会替你编译）：
;      dotnet build "FluentFlyoutWPF\FluentFlyout.csproj" -c Release-SelfContained -p:RuntimeIdentifier=win-arm64
;
;  产出的安装包：installer\Output\FluentFlyout-Setup-<版本>-arm64.exe
;
;  x64（AMD64）版本见同目录的 FluentFlyout.iss。
;
;  卸载行为（与 x64 版一致）：
;    * 程序目录、开始菜单快捷方式、桌面快捷方式（若安装时勾选）一并删除；
;    * 注册表 HKCU\...\CurrentVersion\Run 下的自启动项 "FluentFlyout" 删除；
;    * 数据目录 %APPDATA%\FluentFlyout（settings.xml 与日志）会弹出对话框，
;      由用户选择"是"保留 / "否"删除，默认按钮为"否"（保留）。
; ============================================================================================

#define MyAppName "FluentFlyout"
#define MyAppVersion "2.2.1"
#define MyAppPublisher "Zerin-emm"
#define MyAppUrl "https://github.com/Zerin-emm/FluentFlyout"
#define MyAppExeName "FluentFlyout.exe"
; 显示用的名称，只用于窗口标题与卸载列表，避免和 x64 版在"应用和功能"里同名混淆。
#define MyAppDisplayName "FluentFlyout (ARM64)"

; 要打包的自包含产物目录。
; 注意这里与 x64 脚本的唯一区别：-p:Platform=ARM64 的输出落在 bin\ARM64\ 而不是 bin\x64\。
#define BuildDir "..\FluentFlyoutWPF\bin\ARM64\Release-SelfContained"

; 64 位安装模式下 {autoappdata} 解析到 %APPDATA%；32 位模式下它会退回
; %APPDATA% 的 WOW64 重定向形态，与应用程序实际写入的路径不一致，故直接写路径。
#define AppDataDir "{userappdata}\FluentFlyout"

[Setup]
; ARM64 版用独立的 AppId：两个架构因此可以同时安装、各自独立卸载，
; 互不触发"检测到已安装同一程序、先卸载旧版"的逻辑。
; 不要把它改成 x64 版的 AppId，否则两个包会互相覆盖。
AppId={{7E2D5A18-3B64-4C9F-A1E7-2D8F4B6C9A53}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppDisplayName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName} ARM64
DefaultGroupName={#MyAppDisplayName}
DisableProgramGroupPage=yes
; 程序本身以当前用户身份运行：设置写在 HKCU、数据写在 %APPDATA%、开机自启动项也在 HKCU，
; 所以固定为"仅为我安装"，不请求管理员权限、也不弹安装模式选择页。
PrivilegesRequired=lowest
; 自包含产物里带的是 ARM64 原生运行库，只能装在 ARM64 的 Windows 上。
; Windows 11 ARM64 会用 x64 模拟跑 x64 程序，但"原生 ARM64"才是这个包的意义所在，
; 所以这里收紧到 arm64 而非 x64compatible。
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
; 这是打包好的桌面应用，不是商店应用；不加这条，Win10 会把安装包直接拦住。
MinVersion=10.0.22000
OutputDir=Output
; 文件名里带架构后缀，避免与 x64 版互相覆盖。
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}-arm64
SetupIconFile=..\FluentFlyoutWPF\Resources\FluentFlyout2.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppDisplayName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic windows11
; 覆盖安装 / 卸载时，若程序仍在运行，由 Inno 自己的机制处理（脚本里不再手写杀进程）：
;
;   CloseApplications —— 非静默安装时由 Windows 重启管理器（Restart Manager）在「准备安装」页
;                        自动检测并关闭锁着待更新文件的程序；静默安装时直接关闭。
;                        它按「谁锁着我要写的文件」判断，比按进程名强杀精确，不会误杀同名的
;                        绿色版/开发版。
;   CloseApplicationsFilter —— 只关心我们自己的 exe，别去枚举别的进程。
;   RestartApplications=no —— 装完后不要自动把程序拉起来，启不启动由用户在最后一页决定。
;
; 这里**故意不用 AppMutex**：它虽然也能检测「程序正在运行」，但处理方式是弹一个
; 「请先关闭正在运行的程序，然后点击确定继续」的提示框，而不是自动关闭。实测两种静默场景都会坏：
;   - /VERYSILENT /SUPPRESSMSGBOXES → 提示框被自动当作「取消」，安装直接中止（exit 1）；
;   - /VERYSILENT（不抑制消息框）     → 卡在提示框上等用户点击，静默部署永远不会结束。
; 覆盖安装时 CloseApplications 已经能把程序关掉，所以 AppMutex 只会让静默安装变差。
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
; 安装包与卸载程序的界面语言：Inno Setup 6.6.1 自带的是 Chinese.isl（简体中文）。
; 若你的 Inno Setup 里文件名是 ChineseSimplified.isl，把下面一行改掉即可。
[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\Chinese.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 打包整个 Release-SelfContained 输出目录：exe、依赖 dll、原生运行库、附属资源目录、NLog.config、Resources\* 等。
; Excludes 中的两个文件按需保留：pdb 是调试符号（Release 构建已不再生成，
; 此处只是兜底），xml 是给 IDE 的类型提示文档，运行时用不到。
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{autoprograms}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 选项：安装完立即启动。skipifsilent 让静默安装不弹窗。
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppDisplayName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理安装目录本身（以及早期构建可能留下的 TFM 子目录）。
; 数据目录 %APPDATA%\FluentFlyout 不在这里删——它要由用户在卸载时自己决定，
; 处理逻辑见下面的 [Code] 段。
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}\net10.0-windows10.0.22000.0"

[Code]
const
  { 与 FluentFlyoutWPF\Pages\SystemPage.xaml.cs 里的常量保持一致。
    注意这里用的是不带架构后缀的 "FluentFlyout"，两个架构共用同一个自启动项名，
    因为同一个用户同一时刻只应该有一个 FluentFlyout 随开机启动。 }
  StartupRunKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run';
  StartupRunValueName = '{#MyAppName}';

var
  { 用户在卸载时对"是否删除数据目录"的选择，供 usPostUninstall 使用 }
  DeleteUserData: Boolean;
  DataDirPath: String;
  AskUserDataAnswered: Boolean;

  { InitializeSetup 读到的上次安装目录，供 InitializeWizard 使用 }
  PreviousInstallPath: String;

{ ------------------------------------------------------------------------------------------
  读取「上一版安装到了哪里」。

  Inno 会把每次安装的信息写在 HKCU\...\Uninstall\<AppId>_is1 下，其中
  "Inno Setup: App Path" 就是那一次的安装目录。覆盖安装时用它当默认目录，
  用户以前如果装到了非默认位置（例如 D 盘），升级时就不会被悄悄搬到默认目录去。

  取不到就返回空串，由调用方回退到 DefaultDirName。
  ------------------------------------------------------------------------------------------ }
function GetPreviousInstallPath(): String;
var
  UninstallKey: String;
  PreviousPath: String;
begin
  Result := '';

  { AppId 在脚本里用的是 Inno 的转义写法（以两个左花括号开头），而它展开后仍是那串原始
    文本，直接拼进注册表路径会多出一个左花括号、从而查不到键。
    Inno 自己算卸载键名时做了一次反转义，这里做同样的处理。 }
  UninstallKey := '{#SetupSetting("AppId")}';
  if Copy(UninstallKey, 1, 2) = '{{' then
    UninstallKey := Copy(UninstallKey, 2, Length(UninstallKey) - 1);

  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + UninstallKey + '_is1';

  if RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', PreviousPath)
     and (PreviousPath <> '') then
  begin
    { 只是"记录过"，目录可能已被用户手工删掉，那就当没装过 }
    if DirExists(PreviousPath) then
    begin
      Result := PreviousPath;
      Log('检测到已安装，将复用上次的安装目录：' + Result);
    end
    else
      Log('上次的安装目录已不存在，忽略：' + PreviousPath);
  end
  else
    Log('未检测到已安装记录，使用默认安装目录');
end;

{ ------------------------------------------------------------------------------------------
  记录上一版装在哪里，供 InitializeWizard 改写默认目录用。

  必须在 InitializeSetup 里读（此时还没有 WizardForm，不能直接改 DirEdit），
  而 InitializeSetup 返回 False 会中止安装，所以这里只读、不弹任何界面。
  ------------------------------------------------------------------------------------------ }
function InitializeSetup(): Boolean;
begin
  PreviousInstallPath := GetPreviousInstallPath();
  Result := True;
end;

{ ------------------------------------------------------------------------------------------
  把「选择安装位置」页的默认目录改成上次的安装目录。

  放在 InitializeWizard 而不是 InitializeSetup，因为 WizardForm 到这里才存在。
  ------------------------------------------------------------------------------------------ }
procedure InitializeWizard();
begin
  { 静默安装（/SILENT、/VERYSILENT）时不要去动 DirEdit：
    Inno 已经把 /DIR= 或默认目录写好了，改动它会覆盖调用方的显式指定。 }
  if (PreviousInstallPath <> '') and (not WizardSilent) then
    WizardForm.DirEdit.Text := PreviousInstallPath;
end;

{ ------------------------------------------------------------------------------------------
  卸载时删除开机自启动项。
  不论用户是否保留数据都删：一个已经被卸载的程序不应该继续在开机时尝试启动。
  找不到该项（用户从未开启过自启动）属正常情况，静默跳过。
  ------------------------------------------------------------------------------------------ }
procedure RemoveStartupEntry();
var
  ExistingValue: String;
begin
  if not RegQueryStringValue(HKCU, StartupRunKey, StartupRunValueName, ExistingValue) then
  begin
    { 用户从未开启过开机自启动，注册表里本来就没有这一项 }
    Log('未找到自启动项，无需删除：HKCU\' + StartupRunKey + '\' + StartupRunValueName);
    Exit;
  end;

  if RegDeleteValue(HKCU, StartupRunKey, StartupRunValueName) then
    Log('已删除自启动项 HKCU\' + StartupRunKey + '\' + StartupRunValueName +
        '（原值：' + ExistingValue + '）')
  else
    Log('无法删除自启动项 HKCU\' + StartupRunKey + '\' + StartupRunValueName +
        '（原值：' + ExistingValue + '），可稍后手动清理');
end;

{ ------------------------------------------------------------------------------------------
  询问是否删除数据目录。只在真正的卸载流程里问一次。
  默认按钮是"否"（保留数据），避免用户一路回车把设置和日志误删。
  注意：x64 与 ARM64 两个版本共用 %APPDATA%\FluentFlyout 这一份数据，
  卸载其中一个时若选择删除，另一个版本的设置也会一并消失——这是刻意的：
  用户在同一台机器上通常只用其中一个架构。
  ------------------------------------------------------------------------------------------ }
procedure AskAboutUserData();
begin
  if AskUserDataAnswered then
    Exit;
  AskUserDataAnswered := True;

  if not DirExists(DataDirPath) then
  begin
    DeleteUserData := False;
    Log('数据目录不存在，无需询问：' + DataDirPath);
    Exit;
  end;

  DeleteUserData :=
    MsgBox('是否同时删除 FluentFlyout 的设置与日志？' + #13#10 + #13#10 +
           '数据目录：' + #13#10 + DataDirPath + #13#10 + #13#10 +
           '选择"是"将彻底删除该目录（包括设置、开机自启偏好与日志文件），此操作不可撤销。' + #13#10 +
           '选择"否"将保留该目录，你以后重新安装 FluentFlyout 时设置仍然有效。',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

  if DeleteUserData then
    Log('用户选择删除数据目录：' + DataDirPath)
  else
    Log('用户选择保留数据目录：' + DataDirPath);
end;

{ ------------------------------------------------------------------------------------------
  删除数据目录。DelTree 的第三个参数为 True 表示"整个目录不存在了"，会一并删掉目录本身。
  用户可能把 settings.xml 设成只读（或日志被别的工具占用），删不掉时给出提示而不是静默失败。
  ------------------------------------------------------------------------------------------ }
procedure RemoveUserData();
begin
  if not DeleteUserData then
    Exit;
  if not DirExists(DataDirPath) then
    Exit;

  if DelTree(DataDirPath, True, True, True) then
    Log('已删除数据目录：' + DataDirPath)
  else
    { 卸载程序自身的界面此时可能已经关闭，用 SuppressibleMsgBox 直接弹系统对话框 }
    SuppressibleMsgBox('未能删除数据目录：' + #13#10 + DataDirPath + #13#10 + #13#10 +
                       '请手动删除该文件夹。',
                       mbError, MB_OK, IDOK);
end;

{ ------------------------------------------------------------------------------------------
  卸载流程挂钩：
    usUninstall:      询问是否删除数据、删除开机自启动项；
    usPostUninstall:  此时安装目录已被卸载程序清空，再删数据目录不会与它互相干扰。
  注意：用户点"取消"时不会到达这里，因此上面所有操作只会在卸载真正继续时发生。
  ------------------------------------------------------------------------------------------ }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { 卸载同样不自己杀进程：CloseApplications 会让重启管理器在需要时关闭占用文件的程序 }
    DataDirPath := ExpandConstant('{#AppDataDir}');
    AskAboutUserData();
    RemoveStartupEntry();
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveUserData();
  end;
end;
