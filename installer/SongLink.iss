; SongLink 点歌台 — Inno Setup 安装器脚本
; 编译:  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\SongLink.iss
; 输出:  dist\SongLink_Setup.exe

#define MyAppName "SongLink 点歌台"
#define MyAppShortName "SongLink"
#define MyAppPublisher "SongLink"
#define MyAppURL "https://github.com/Hermitweb/songlink"
#define MyAppExeName "SongLink.exe"

[Setup]
; 基础信息
AppId={{C0B1B5C0-3A2D-4E5F-9D8B-1234567890AB}
AppName={#MyAppName}
AppVersion=1.0.0
AppVerName={#MyAppName} v1.0.0
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=Copyright (c) 2026 SongLink
; 路径
DefaultDirName={localappdata}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 输出
OutputDir=..\dist
OutputBaseFilename=SongLink_Setup_v1.0.0
; 压缩与签名
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; 视觉
WizardStyle=modern
WizardSizePercent=120
; 权限
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 杂项
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "开机自动启动 SongLink"; GroupDescription: "其他任务:"; Flags: unchecked

[Files]
; 整个发布目录(自包含单文件 exe)
Source: "..\dist\SongLink-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 注意:Source 不会被打包器重复构建,只是把已发布的目录内容复制进安装包

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppShortName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SongLink"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\{#MyAppShortName}\logs"

[Code]
// 安装前:检测是否已装,可选复用旧版本号
function InitializeSetup(): Boolean;
var
    ResultCode: Integer;
begin
    Result := True;
end;

// 卸载前:尝试关闭运行中的实例
function InitializeUninstall(): Boolean;
begin
    Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
    Result := True;
end;