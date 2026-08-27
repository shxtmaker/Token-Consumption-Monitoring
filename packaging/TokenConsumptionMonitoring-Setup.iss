; Token Consumption Monitoring V1.2.0 安装脚本（Inno Setup 6）
; 前置：先执行 dotnet publish -c Release -o publish（见 README 构建章节）
#define MyAppName "TokenConsumptionMonitoring"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "shxtmaker"
#define MyAppExeName "TokenConsumptionMonitoring.exe"

[Setup]
; 使用独立 AppId，名称迁移不声明对旧安装的覆盖升级。
AppId={{C5D7E9A1-4B62-4F38-9A07-8E1C3D6B2A54}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/shxtmaker/Token-Consumption-Monitoring
DefaultDirName={autopf}\TokenConsumptionMonitoring
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=TokenConsumptionMonitoring-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillApp"
