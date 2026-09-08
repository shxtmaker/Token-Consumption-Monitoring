; Build through packaging/release.ps1 with Inno Setup 6.
#ifndef MyAppVersion
  #error MyAppVersion must be supplied by the release script.
#endif
#ifndef PublishDir
  #error PublishDir must point to the complete self-contained publish output.
#endif
#ifndef PackageOutputDir
  #error PackageOutputDir must be supplied by the release script.
#endif
#ifndef PackageMode
  #define PackageMode "Setup"
#endif
#if PackageMode != "Setup" && PackageMode != "Upgrade"
  #error PackageMode must be Setup or Upgrade.
#endif

#define MyAppName "TokenConsumptionMonitoring"
#define MyAppExeName MyAppName + ".exe"
#define MyAppPublisher "shxtmaker"

; Production identity is intentionally not configurable. Verification builds have
; a separate identity, mutex and shortcuts, and must never be distributed.
#ifdef VerificationBuild
  #ifndef VerificationAppId
    #error VerificationAppId is required for isolated verification.
  #endif
  #define ProductAppId "{{" + VerificationAppId + "}"
  #define ProductRegistryId "{" + VerificationAppId + "}_is1"
  #define ProductMutex MyAppName + "_Verification_" + VerificationAppId
  #define ProductDisplayName MyAppName + " Packaging Verification"
#else
  #define ProductAppId "{{C5D7E9A1-4B62-4F38-9A07-8E1C3D6B2A54}"
  #define ProductRegistryId "{C5D7E9A1-4B62-4F38-9A07-8E1C3D6B2A54}_is1"
  #define ProductMutex "TokenConsumptionMonitoring_SingleInstance"
  #define ProductDisplayName MyAppName
#endif

[Setup]
AppId={#ProductAppId}
AppName={#ProductDisplayName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/shxtmaker/Token-Consumption-Monitoring
DefaultDirName={code:GetInstallDirectory}
DefaultGroupName={#ProductDisplayName}
DisableProgramGroupPage=yes
OutputDir={#PackageOutputDir}
OutputBaseFilename=TokenConsumptionMonitoring-{#PackageMode}-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=yes
UsePreviousAppDir=yes
AppMutex={#ProductMutex}
SetupMutex={#ProductMutex}_Installer
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
#if PackageMode == "Upgrade"
DisableDirPage=yes
DisableReadyPage=yes
UsePreviousTasks=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

#ifndef VerificationBuild
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifndef VerificationBuild
[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
#endif

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductRegistryId}';
  InvalidHandle = -1;
  GenericRead = $80000000;
  OpenExisting = 3;
  FileAttributeNormal = $80;
  FileAttributeReparsePoint = $400;

var
  InstalledDirectory: String;

function CreateFile(FileName: String; DesiredAccess, ShareMode, SecurityAttributes,
  CreationDisposition, FlagsAndAttributes, TemplateFile: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function InstallationRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKLM64 else Result := HKCU64;
end;

function GetInstallDirectory(Param: String): String;
begin
  if InstalledDirectory <> '' then Result := InstalledDirectory
  else Result := ExpandConstant('{autopf}\{#MyAppName}');
end;

function NextVersionPart(var Version: String): Integer;
var
  Separator: Integer;
  Part: String;
begin
  Separator := Pos('.', Version);
  if Separator = 0 then begin
    Part := Version;
    Version := '';
  end else begin
    Part := Copy(Version, 1, Separator - 1);
    Delete(Version, 1, Separator);
  end;
  Result := StrToIntDef(Part, 0);
end;

function IsNewerVersion(Version, Target: String): Boolean;
var
  I, CurrentPart, TargetPart: Integer;
begin
  Result := False;
  for I := 1 to 4 do begin
    CurrentPart := NextVersionPart(Version);
    TargetPart := NextVersionPart(Target);
    if CurrentPart <> TargetPart then begin
      Result := CurrentPart > TargetPart;
      Exit;
    end;
  end;
end;

function ReadInstallation: Boolean;
var
  Uninstaller: String;
begin
  InstalledDirectory := '';
  Result := RegQueryStringValue(InstallationRoot, UninstallKey,
    'InstallLocation', InstalledDirectory);
  if Result then begin
    InstalledDirectory := RemoveBackslash(InstalledDirectory);
    Result := (InstalledDirectory <> '') and
      FileExists(InstalledDirectory + '\{#MyAppExeName}') and
      RegQueryStringValue(InstallationRoot, UninstallKey, 'UninstallString', Uninstaller);
    if Result then Result := FileExists(RemoveQuotes(Uninstaller));
  end;
  if not Result then InstalledDirectory := '';
end;

function InitializeSetup: Boolean;
var
  InstalledVersion: String;
  FoundInstallation: Boolean;
begin
  FoundInstallation := ReadInstallation;
  Result := True;
#if PackageMode == "Upgrade"
  if not FoundInstallation then begin
    SuppressibleMsgBox('No matching TokenConsumptionMonitoring installation was found ' +
      'in the selected installation scope. Use the Setup package for a first installation, ' +
      'or select the scope of the existing installation (/CURRENTUSER or /ALLUSERS).',
      mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
#endif
  if FoundInstallation and
    GetVersionNumbersString(InstalledDirectory + '\{#MyAppExeName}', InstalledVersion) and
    IsNewerVersion(InstalledVersion, '{#MyAppVersion}') then begin
    SuppressibleMsgBox('A newer version is already installed. Downgrades are not supported.',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function FindLockedFile(Directory: String): String;
var
  FindRec: TFindRec;
  Path: String;
  Handle: Integer;
begin
  Result := '';
  if not DirExists(Directory) then Exit;
  if FindFirst(AddBackslash(Directory) + '*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
          (Pos('unins', Lowercase(FindRec.Name)) <> 1) then begin
          Path := AddBackslash(Directory) + FindRec.Name;
          if (FindRec.Attributes and FileAttributeReparsePoint) <> 0 then begin
            Result := Path;
            Exit;
          end;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            Result := FindLockedFile(Path)
          else begin
            Handle := CreateFile(Path, GenericRead, 0, 0, OpenExisting, FileAttributeNormal, 0);
            if Handle = InvalidHandle then Result := Path
            else CloseHandle(Handle);
          end;
          if Result <> '' then Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  LockedFile: String;
begin
  Result := '';
#if PackageMode == "Upgrade"
  if not ReadInstallation then begin
    Result := 'The matching installation is no longer available. Run the Setup package.';
    Exit;
  end;
  if CompareText(RemoveBackslash(ExpandConstant('{app}')), InstalledDirectory) <> 0 then begin
    Result := 'An upgrade must use the existing installation directory.';
    Exit;
  end;
#endif
  if CheckForMutexes('{#ProductMutex}') then begin
    Result := 'Exit TokenConsumptionMonitoring from its tray menu before continuing.';
    Exit;
  end;
  LockedFile := FindLockedFile(ExpandConstant('{app}'));
  if LockedFile <> '' then
    Result := 'A file is locked or the directory contains a link: ' + LockedFile + #13#10 +
      'Exit the application and close programs using this directory, then retry.';
end;

function InitializeUninstall: Boolean;
var
  LockedFile: String;
begin
  if CheckForMutexes('{#ProductMutex}') then begin
    SuppressibleMsgBox('Exit TokenConsumptionMonitoring from its tray menu before uninstalling.',
      mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  LockedFile := FindLockedFile(ExpandConstant('{app}'));
  Result := LockedFile = '';
  if not Result then
    SuppressibleMsgBox('Close programs using the installation directory before uninstalling: ' +
      LockedFile, mbError, MB_OK, IDOK);
end;
