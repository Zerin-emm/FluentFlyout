; ============================================================================================
;  FluentFlyout Inno Setup 安装脚本 —— x64（AMD64）自包含版
; --------------------------------------------------------------------------------------------
;  编译方式（Inno Setup 6.x）：
;      "D:\Inno Setup 6.6.1\ISCC.exe" installer\FluentFlyout.iss
;  或直接双击 / 用 Compil32.exe 打开本文件后点“编译”。
;
;  编译之前请先构建程序本体（脚本只打包 bin\...\Release-SelfContained 的产物，不会替你编译）：
;      dotnet build "FluentFlyoutWPF\FluentFlyout.csproj" -c Release-SelfContained -p:Platform=x64
;
;  产出的安装包：installer\Output\FluentFlyout-Setup-<版本>-x64.exe
;
;  ARM64 版本见同目录的 FluentFlyout-arm64.iss。
;
;  卸载行为：
;    * 程序目录、开始菜单快捷方式、桌面快捷方式（若安装时勾选）一并删除；
;    * 注册表 HKCU\...\CurrentVersion\Run 下的自启动项 "FluentFlyout" 删除；
;    * 数据目录 %APPDATA%\FluentFlyout（settings.xml 与日志）会弹出对话框，
;      由用户选择"是"保留 / "否"删除，默认按钮为"否"（保留）。
; ============================================================================================

#define MyAppName "FluentFlyout"
#define MyAppVersion "2.2.0"
#define MyAppPublisher "Zerin-emm"
#define MyAppUrl "https://github.com/Zerin-emm/FluentFlyout"
#define MyAppExeName "FluentFlyout.exe"

; 要打包的自包含产物目录。若哪天输出路径又变了，只改这一行即可。
; 注意 -p:Platform=ARM64 时会落到 bin\ARM64\，所以 ARM64 脚本里这一行不同。
#define BuildDir "..\FluentFlyoutWPF\bin\x64\Release-SelfContained"

; 64 位安装模式下 {autoappdata} 解析到 %APPDATA%；32 位模式下它会退回
; %APPDATA% 的 WOW64 重定向形态，与应用程序实际写入的路径不一致，故直接写路径。
#define AppDataDir "{userappdata}\FluentFlyout"

[Setup]
; AppId 一旦发布就不要再改，否则新版本会被当成另一个软件、无法覆盖安装与升级。
; ARM64 版用的是另一个 AppId，两个架构可以并存、各自独立卸载。
AppId={{9C4B7E06-4F2A-4E1B-9E3D-0A6F5C8B1D47}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 程序本身以当前用户身份运行：设置写在 HKCU、数据写在 %APPDATA%、开机自启动项也在 HKCU，
; 所以固定为"仅为我安装"，不请求管理员权限、也不弹安装模式选择页。
PrivilegesRequired=lowest
; 自包含产物里带的是 x64 原生运行库，所以只允许装到 x64 系统上。
; （ARM64 上的 x64 模拟也能跑，但既然另有 ARM64 版，这里就收紧到真正的 x64。）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 这是打包好的桌面应用，不是商店应用；不加这条，Win10 会把安装包直接拦住。
MinVersion=10.0.22000
OutputDir=Output
; 文件名里带架构后缀，避免两个架构的安装包互相覆盖。
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}-x64
SetupIconFile=..\FluentFlyoutWPF\Resources\FluentFlyout2.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic windows11
; 安装包与卸载程序的界面语言：Inno Setup 6.6.1 自带的是 Chinese.isl（简体中文）。
; 若你的 Inno Setup 里文件名是 ChineseSimplified.isl，把下面一行改掉即可。
[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\Chinese.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 打包整个 Release 输出目录：exe、依赖 dll、NLog.config、Resources\* 等。
; Excludes 中的两个文件按需保留：pdb 是调试符号（Release 构建已不再生成，
; 此处只是兜底），xml 是给 IDE 的类型提示文档，运行时用不到。
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 选项：安装完立即启动。skipifsilent 让静默安装不弹窗。
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理安装目录本身（以及早期构建可能留下的 TFM 子目录）。
; 数据目录 %APPDATA%\FluentFlyout 不在这里删——它要由用户在卸载时自己决定，
; 处理逻辑见下面的 [Code] 段。
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}\net10.0-windows10.0.22000.0"

[Code]
const
  { 与 FluentFlyoutWPF\Pages\SystemPage.xaml.cs 里的常量保持一致 }
  StartupRunKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run';
  StartupRunValueName = '{#MyAppName}';

var
  { 用户在卸载时对"是否删除数据目录"的选择，供 usPostUninstall 使用 }
  DeleteUserData: Boolean;
  DataDirPath: String;
  AskUserDataAnswered: Boolean;

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
    usUninstall:      停掉正在运行的程序、询问是否删除数据、删除开机自启动项；
    usPostUninstall:  此时安装目录已被卸载程序清空, 再删数据目录不会与它互相干扰。
  注意：用户点"取消"时不会到达这里, 因此上面所有操作只会在卸载真正继续时发生。
  ------------------------------------------------------------------------------------------ }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    DataDirPath := ExpandConstant('{#AppDataDir}');
    AskAboutUserData();
    RemoveStartupEntry();
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveUserData();
  end;
end;
