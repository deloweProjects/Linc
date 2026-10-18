; Linc for Windows — the installer (1.0.0.11, D-085). Inno Setup 6.
;
; Built by Linc\tools\release.ps1, which passes:
;   /DAppVersion=1.0.0.11 /DDisplayName=1.0.0-beta.11 /DSourceDir=<publish folder> /DOutputDir=<dir>
;   and, only when a code-signing certificate is configured, /DSign plus /Slinc=<signtool command>.
;
; Per-user, no UAC: installs to %LOCALAPPDATA%\Programs\Linc. Upgrades in place (stable AppId),
; closing a running Linc first. Linc's own data (%LOCALAPPDATA%\Linc: pairing, history, logs) is
; never touched by an upgrade; the uninstaller asks whether to delete it.

#ifndef AppVersion
  #error Pass /DAppVersion=1.0.0.x (release.ps1 does)
#endif
#ifndef DisplayName
  #define DisplayName AppVersion
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<the published Linc folder> (release.ps1 does)
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppExe "Linc.Desktop.exe"

[Setup]
; Never change this GUID: it is how Windows (and the next installer) knows this is Linc.
AppId={{8C3E9B2A-6F41-4D7B-9A2E-3C5D1B7F0A11}
AppName=Linc
AppVersion={#AppVersion}
AppVerName=Linc {#DisplayName}
VersionInfoVersion={#AppVersion}
AppPublisher=deloweProjects
AppPublisherURL=https://github.com/deloweProjects/Linc
AppSupportURL=https://github.com/deloweProjects/Linc/issues
AppUpdatesURL=https://github.com/deloweProjects/Linc/releases
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\Linc
; Fixed location (per user, no admin). Fixed on purpose: the uninstaller removes the whole folder.
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Linc
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Linc-Setup-{#AppVersion}
SetupIconFile=..\DESKTOP\Linc.Desktop\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Linc
LicenseFile=..\..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=force
CloseApplicationsFilter={#AppExe}
RestartApplications=no
#ifdef Sign
SignTool=linc
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start Linc when I sign in to Windows (it waits quietly in the notification area)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Linc"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\Linc"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The same Run entry Linc's own "Start with Windows" setting writes (the app adopts it on start).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Linc"; \
  ValueData: """{app}\{#AppExe}"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Linc"; Flags: nowait postinstall skipifsilent
; A silent run is Linc updating itself: start it again afterwards.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Close a running Linc the way its tray Exit does, then put back what it changed on the PC
; (default and Voice Access microphones, display scale, Explorer menu, autostart). VB-CABLE stays.
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "LincQuit"
Filename: "{app}\{#AppExe}"; Parameters: "--restore-and-exit"; Flags: runhidden waituntilterminated; RunOnceId: "LincRestore"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  QuitWaitMs = 6000;

function IsAtLeast1011(const Path: String): Boolean;
var
  Version: Int64;
begin
  { Only 1.0.0.11 and later understand --quit; an older exe would take it for a file to share. }
  Result := GetPackedVersion(Path, Version) and
    (ComparePackedVersion(Version, PackVersionComponents(1, 0, 0, 11)) >= 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) and IsAtLeast1011(Exe) then
  begin
    { Ask the running Linc to exit cleanly before its files are replaced; Restart Manager
      (CloseApplications=force) is the fallback if it doesn't. }
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Linc');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Also delete Linc''s data on this PC?' + #13#10#13#10 +
                'That is your paired phones, clipboard and notification history, saved settings and logs (' + DataDir + ').' + #13#10#13#10 +
                'Choose No to keep them, for example if you will install Linc again.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
