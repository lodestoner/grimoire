; Local source candidate. Version values are required from build.ps1 / Version.props.
#ifndef AppVersion
  #error AppVersion must be supplied by build.ps1
#endif
#ifndef NumericVersion
  #error NumericVersion must be supplied by build.ps1
#endif
#define BuiltExe AddBackslash(SourcePath) + "..\dist\Grimoire.exe"
#if GetVersionNumbersString(BuiltExe) != NumericVersion
  #error Executable numeric version disagrees with Version.props
#endif
#if GetStringFileInfo(BuiltExe, "ProductVersion") != AppVersion
  #error Executable prerelease version disagrees with Version.props
#endif
#define MyAppName "Grimoire"
#define MyAppExe "Grimoire.exe"
#define MyAppPublisher "lodestoner"
#define MyAppURL "https://github.com/lodestoner/grimoire"

[Setup]
AppId={{B6E0C3A2-7D3F-4C7E-9B7A-2F5A6C1D8E90}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion} (source candidate)
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename={#MyAppName}-Setup-{#AppVersion}
SetupIconFile=..\app\Grimoire\grimoire.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=no
RestartApplications=no
UsePreviousTasks=no
; 2.1 logged a global taskkill and unconditional startup deletion. The candidate
; records the complete owned file/shortcut set, replacing that legacy uninstall log.
UninstallLogMode=overwrite
LicenseFile=..\LICENSE
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#MyAppName} source candidate installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} when I sign in to Windows"; GroupDescription: "Options:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\dist\Grimoire.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
var
  StartupTaskDirectory: String;

function HasTaskOverride(): Boolean;
var I: Integer; Arg: String;
begin
  Result := False;
  for I := 1 to ParamCount do begin
    Arg := Uppercase(ParamStr(I));
    if (Pos('/TASKS=', Arg) = 1) or (Pos('/MERGETASKS=', Arg) = 1) then Result := True;
  end;
end;

function OwnStartup(): Boolean;
var Value: String;
begin
  Result := RegQueryStringValue(HKCU, RunKey, '{#MyAppName}', Value) and
    (CompareText(Value, '"' + ExpandConstant('{app}\{#MyAppExe}') + '"') = 0);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not HasTaskOverride() and
     (CompareText(StartupTaskDirectory, ExpandConstant('{app}')) <> 0) then begin
    StartupTaskDirectory := ExpandConstant('{app}');
    if OwnStartup() then WizardSelectTasks('startup') else WizardSelectTasks('!startup');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Result := '';
  { Use NEW code, even when the target is 2.1. This helper never launches the target. }
  ExtractTemporaryFile('{#MyAppExe}');
  if not Exec(ExpandConstant('{tmp}\{#MyAppExe}'),
      '--quit-target "' + ExpandConstant('{app}\{#MyAppExe}') + '"', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Result := 'Quit Grimoire from its tray menu, then retry. The installed executable is still busy or inaccessible.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var EnableStartup: Boolean;
begin
  if CurStep = ssPostInstall then begin
    if WizardSilent() and not HasTaskOverride() then EnableStartup := OwnStartup()
    else EnableStartup := WizardIsTaskSelected('startup');
    if EnableStartup then
      RegWriteStringValue(HKCU, RunKey, '{#MyAppName}', '"' + ExpandConstant('{app}\{#MyAppExe}') + '"')
    else if OwnStartup() then RegDeleteValue(HKCU, RunKey, '{#MyAppName}');
  end;
end;

{ Setup's script engine is 32-bit and has no NativeInt type; kernel handles fit in an Integer,
  and INVALID_HANDLE_VALUE compares as -1. }
function CreateFileW(Name: String; Access, Share: Cardinal; Security: Integer;
  Creation, Flags: Cardinal; Template: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function TargetAvailable(): Boolean;
var Handle: Integer; Target: String;
begin
  Target := ExpandConstant('{app}\{#MyAppExe}');
  if not FileExists(Target) then begin Result := True; exit; end;
  { GENERIC_WRITE only: a running or locked executable denies it, and the value stays inside
    the script engine's signed 32-bit integer range. }
  Handle := CreateFileW(Target, $40000000, 0, 0, 3, 0, 0);
  Result := Handle <> -1;
  if Result then CloseHandle(Handle);
end;

function InitializeUninstall(): Boolean;
var MS, LS: Cardinal; Code: Integer; Helper: String;
begin
  Result := TargetAvailable();
  if Result then exit;
  { A user may have restored an old executable. Never send it an unknown option. }
  if GetVersionNumbers(ExpandConstant('{app}\{#MyAppExe}'), MS, LS) and
     (MS >= $00020002) then begin
    Helper := ExpandConstant('{tmp}\Grimoire-uninstall-helper.exe');
    if FileCopy(ExpandConstant('{app}\{#MyAppExe}'), Helper, False) then begin
      if Exec(Helper, '--quit-target "' + ExpandConstant('{app}\{#MyAppExe}') + '"', '',
          SW_HIDE, ewWaitUntilTerminated, Code) then Result := (Code = 0) and TargetAvailable();
      DeleteFile(Helper);
    end;
  end;
  if not Result and not UninstallSilent() then
    MsgBox('Quit Grimoire from its tray menu, then retry uninstall.', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Shell, Shortcut: Variant; Link: String; I: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    if OwnStartup() then RegDeleteValue(HKCU, RunKey, '{#MyAppName}');
    for I := 0 to 1 do begin
      if I = 0 then Link := ExpandConstant('{userappdata}\Microsoft\Windows\SendTo\{#MyAppName}.lnk')
      else Link := ExpandConstant('{autodesktop}\{#MyAppName}.lnk');
      if FileExists(Link) then begin
      try
        Shell := CreateOleObject('WScript.Shell');
        Shortcut := Shell.CreateShortcut(Link);
        if CompareText(Shortcut.TargetPath, ExpandConstant('{app}\{#MyAppExe}')) = 0 then DeleteFile(Link);
      except
        Log('Could not inspect owned shortcut; retained it.');
      end;
      end;
    end;
  end;
  { Always retain settings, spells and Credential Manager. No deletion prompts. }
end;
