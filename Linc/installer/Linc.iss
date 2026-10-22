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
; LincLink: the phone dials this PC and its "I'm here" beacon reaches it only if Windows Firewall
; lets Linc in. One rule, for Linc only, on private networks (home, work; never public). It needs
; administrator permission once, so Windows asks; the install itself still doesn't. Without it,
; the PC still finds the phone by dialling out, which the firewall never blocks.
Name: "firewall"; Description: "Let Linc through Windows Firewall on home networks (Windows asks for permission once)"; GroupDescription: "Connection:"

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
; The firewall rule (see [Tasks]) is added from [Code] (AddFirewallRule), so declining Windows'
; permission prompt is quietly accepted instead of showing an error.
Filename: "{app}\{#AppExe}"; Description: "Launch Linc"; Flags: nowait postinstall skipifsilent
; A silent run is Linc updating itself: start it again afterwards.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Close a running Linc the way its tray Exit does, then put back what it changed on the PC
; (default and Voice Access microphones, display scale, Explorer menu, autostart). VB-CABLE stays.
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "LincQuit"
Filename: "{app}\{#AppExe}"; Parameters: "--restore-and-exit"; Flags: runhidden waituntilterminated; RunOnceId: "LincRestore"
; The firewall rule goes with Linc, from [Code] (RemoveFirewallRule): only when it is there.

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  QuitWaitMs = 6000;
  { LincLink's own firewall rule. Its own name, so only this rule is ever replaced or removed,
    never one Windows or the owner made for Linc (a "Linc" rule allowing a public network, say). }
  FirewallRule = 'Linc-LincLink';

function Netsh: String;
begin
  Result := ExpandConstant('{sys}\netsh.exe');
end;

{ The firewall task (see [Tasks]): one elevated step that replaces this rule, so reinstalling never
  stacks them. Windows asks for permission; if the owner declines, Linc goes without the rule (the
  PC still reaches the phone by dialling out), and Setup says nothing. A silent run is Linc
  updating itself: it never asks (the rule, if wanted, is there from the first install). }
procedure AddFirewallRule;
var
  Params: String;
  Code: Integer;
begin
  Params := '/s /c ""' + Netsh + '" advfirewall firewall delete rule name=' + FirewallRule + ' >nul 2>&1 & "' +
    Netsh + '" advfirewall firewall add rule name=' + FirewallRule + ' dir=in action=allow "program=' +
    ExpandConstant('{app}\{#AppExe}') + '" enable=yes profile=private,domain"';
  WizardForm.StatusLabel.Caption := 'Letting Linc through Windows Firewall...';
  if not ShellExec('runas', ExpandConstant('{sys}\cmd.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('Firewall rule not added (' + SysErrorMessage(Code) + '); Linc works without it.');
end;

{ On uninstall: remove the rule only if it exists, so a PC that never had it isn't asked for permission. }
procedure RemoveFirewallRule;
var
  Code: Integer;
begin
  if Exec(Netsh, 'advfirewall firewall show rule name=' + FirewallRule, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    if not ShellExec('runas', Netsh, 'advfirewall firewall delete rule name=' + FirewallRule, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Log('Firewall rule left in place (' + SysErrorMessage(Code) + ').');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('firewall') and not WizardSilent then
    AddFirewallRule;
end;

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
  if (CurUninstallStep = usUninstall) and not UninstallSilent then
    RemoveFirewallRule;
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
