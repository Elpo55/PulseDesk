; Sysora installer (Inno Setup 6).
;
; Installs the self-contained build produced by `dotnet publish` into Program Files (or, if the user chooses
; "Install for me only", into %LOCALAPPDATA%\Programs), with a Start menu shortcut and an uninstaller registered
; in Settings › Apps. Built by .github/workflows/release.yml; locally:
;
;   dotnet publish src/Sysora.App -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64
;   ISCC.exe /DAppVersion=1.0.0 /DArch=x64 installer\Sysora.iss
;
; The user's data (%LOCALAPPDATA%\Sysora: settings, history, logs) is kept on uninstall unless the user asks
; to delete it.
;
; Sysora was previously released as PulseDesk. The AppId is unchanged, so Windows treats this as an upgrade: an
; existing PulseDesk installation is replaced (its folder and shortcuts are removed), and Sysora itself moves the
; user's data and its "start with Windows" entry to the new name on its first start.

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

#define AppName "Sysora"
#define AppExe "Sysora.exe"
#define AppPublisher "Elpo55"
#define AppUrl "https://github.com/Elpo55/Sysora"
#define AppTagline "Your PC, Explained."
; Name of the previous releases: folder, executable, shortcuts and startup entry cleaned up on upgrade.
#define LegacyAppName "PulseDesk"

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
VersionInfoDescription={#AppName} installer - {#AppTagline}
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
SetupIconFile=..\src\Sysora.App\Assets\Sysora.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Close a running Sysora (through the Restart Manager) before replacing its files.
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup-{#Arch}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
english.DeleteUserData=Do you also want to delete your Sysora data (settings, local history and logs)?%n%nChoose No to keep it for a later installation.
french.DeleteUserData=Voulez-vous aussi supprimer vos données Sysora (paramètres, historique local et journaux) ?%n%nChoisissez Non pour les conserver en vue d'une réinstallation.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Shortcuts of the previous name (the previous folder itself is removed after installation, see [Code]).
Type: files; Name: "{autoprograms}\{#LegacyAppName}.lnk"
Type: files; Name: "{autodesktop}\{#LegacyAppName}.lnk"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppTagline}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Sysora can register itself to start with Windows (Settings). Remove that entry when it is uninstalled,
; without ever creating it here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#LegacyAppName}"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  // Folder of an installation made under the previous name, when this setup upgrades one.
  LegacyDir: String;

// Sysora keeps running in the notification area when its window is closed. Stop the copies started from the
// installation folder before replacing or removing their files; other copies (a development build, for example)
// are left alone. Copies running under the previous name are stopped too (upgrades).
procedure StopInstalledCopies(const Dir: String);
var
  ResultCode: Integer;
begin
  if not DirExists(Dir) then
    exit;

  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-Process -Name {#AppName},{#LegacyAppName} -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and $_.Path.StartsWith(''' + AddBackslash(Dir) + ''', ''OrdinalIgnoreCase'') } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// An upgrade proposes the folder of the previous installation. When that folder is named after the previous name,
// install into a folder named after Sysora next to it instead; the old folder is removed once installation succeeds.
procedure InitializeWizard;
var
  Dir: String;
begin
  Dir := RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text);
  if (CompareText(ExtractFileName(Dir), '{#LegacyAppName}') = 0) and FileExists(AddBackslash(Dir) + '{#LegacyAppName}.exe') then
  begin
    LegacyDir := Dir;
    WizardForm.DirEdit.Text := ExtractFilePath(Dir) + '{#AppName}';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if LegacyDir <> '' then
    StopInstalledCopies(LegacyDir);
  StopInstalledCopies(WizardDirValue);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Only a folder that still holds the previous executable, and is not the one just installed to, is removed.
  if (CurStep = ssPostInstall) and (LegacyDir <> '')
    and (CompareText(LegacyDir, RemoveBackslashUnlessRoot(WizardDirValue)) <> 0)
    and FileExists(AddBackslash(LegacyDir) + '{#LegacyAppName}.exe') then
    DelTree(LegacyDir, True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, LegacyDataDir: String;
begin
  if CurUninstallStep = usUninstall then
    StopInstalledCopies(ExpandConstant('{app}'));

  if CurUninstallStep <> usPostUninstall then
    exit;

  DataDir := ExpandConstant('{localappdata}\{#AppName}');
  // Data still under the previous name: Sysora moves it on its first start, which may not have happened yet.
  LegacyDataDir := ExpandConstant('{localappdata}\{#LegacyAppName}');
  // Silent uninstalls (scripts, upgrades) never delete user data.
  if UninstallSilent or not (DirExists(DataDir) or DirExists(LegacyDataDir)) then
    exit;

  if MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
    DelTree(LegacyDataDir, True, True, True);
  end;
end;
