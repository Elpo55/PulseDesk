; PulseDesk installer (Inno Setup 6).
;
; Installs the self-contained build produced by `dotnet publish` into Program Files (or, if the user chooses
; "Install for me only", into %LOCALAPPDATA%\Programs), with a Start menu shortcut and an uninstaller registered
; in Settings › Apps. Built by .github/workflows/release.yml; locally:
;
;   dotnet publish src/PulseDesk.App -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64
;   ISCC.exe /DAppVersion=1.0.0 /DArch=x64 installer\PulseDesk.iss
;
; The user's data (%LOCALAPPDATA%\PulseDesk: settings, history, logs) is kept on uninstall unless the user asks
; to delete it.

#ifndef AppVersion
  #error Pass the version: ISCC /DAppVersion=1.2.3
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "PulseDesk"
#define AppExe "PulseDesk.exe"
#define AppPublisher "Elpo55"
#define AppUrl "https://github.com/Elpo55/PulseDesk"

[Setup]
; Never change the AppId: Windows uses it to recognize upgrades of the same application.
AppId={{30092E33-1870-416D-9873-E8D6E440103B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for all users (administrator) by default; the first page offers "Install for me only" instead.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UsedUserAreasWarning=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; Windows 10 version 1809, the minimum supported by the Windows App SDK.
MinVersion=10.0.17763
LicenseFile=..\LICENSE
SetupIconFile=..\src\PulseDesk.App\Assets\PulseDesk.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Close a running PulseDesk (through the Restart Manager) before replacing its files.
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup-{#Arch}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
english.DeleteUserData=Do you also want to delete your PulseDesk data (settings, local history and logs)?%n%nChoose No to keep it for a later installation.
french.DeleteUserData=Voulez-vous aussi supprimer vos données PulseDesk (paramètres, historique local et journaux) ?%n%nChoisissez Non pour les conserver en vue d'une réinstallation.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Local Windows system dashboard"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; PulseDesk can register itself to start with Windows (Settings). Remove that entry when it is uninstalled,
; without ever creating it here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PulseDesk"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// PulseDesk keeps running in the notification area when its window is closed. Stop the copies started from the
// installation folder before replacing or removing their files; other copies (a development build, for example)
// are left alone.
procedure StopInstalledCopies(const Dir: String);
var
  ResultCode: Integer;
begin
  if not DirExists(Dir) then
    exit;

  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-Process -Name PulseDesk -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and $_.Path.StartsWith(''' + AddBackslash(Dir) + ''', ''OrdinalIgnoreCase'') } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopInstalledCopies(WizardDirValue);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
    StopInstalledCopies(ExpandConstant('{app}'));

  if CurUninstallStep <> usPostUninstall then
    exit;

  DataDir := ExpandConstant('{localappdata}\PulseDesk');
  // Silent uninstalls (scripts, upgrades) never delete user data.
  if UninstallSilent or not DirExists(DataDir) then
    exit;

  if MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
