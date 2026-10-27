; RoseMCP installer.
;
; Compiled by deploy.ps1 from the same staged tree the zip is made of, so the installer and the
; archive carry identical bytes and differ only in how they are laid down. ISCC is invoked with
; /DStageDir and /DAppVersion; nothing here discovers either, because a build that guesses its own
; version is a build that can disagree with the binaries it contains.
;
; One installer, both architectures. Choosing between two downloads is the step people get wrong --
; an x64 build on an ARM64 laptop installs and runs, emulated, with no native debug host, and nothing
; says so -- and the [Files] Check: conditions below lay down only what the machine can execute. So
; the download is the only thing that is bigger; the install is the size it always was.
;
; Requires Inno Setup 6.3 or later, for the x64os/arm64 architecture identifiers and the
; IsX64OS/IsArm64 support functions. 6.2 substitutes the deprecated x64 identifier and would install
; the x64 payload onto ARM64.

#ifndef StageDir
  #error StageDir must be passed to ISCC: /DStageDir=<path to artifacts/stage/win>
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "RoseMCP"
#define AppPublisher "BinaryVibrance"
#define AppUrl "https://github.com/AtomicBlom/RoseMCP"
#define DefaultPort "5077"

[Setup]
; A stable GUID is what makes the next installer recognise this one and upgrade in place rather than
; sitting beside it. It never changes, whatever the product is renamed to.
AppId={{8F5D6A21-3C7E-4B0A-9D14-2E6F8A1B7C93}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Per-user throughout, and it needs to stay that way: the install root, settings.json, the log
; folder and the startup registration are all per-user already, and a machine-wide install would put
; a warm Roslyn host holding somebody's solution somewhere every account shares.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\BinaryVibrance\RoseMCP
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; The install root is the same folder the logs and settings already use, so moving it separates an
; install from its own state. Offered anyway, because somebody keeping tools on another drive has a
; reason to.
DisableDirPage=no
UsePreviousAppDir=yes

; x64os rather than x64compatible: ARM64 Windows is x64-compatible through emulation, so
; x64compatible would accept an ARM64 machine and this installer would have to decide what that
; means. It carries both payloads and picks per architecture instead -- see [Files].
ArchitecturesAllowed=x64os or arm64
ArchitecturesInstallIn64BitMode=x64os or arm64

OutputDir={#StageDir}\..\..
OutputBaseFilename=rosemcp-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Out of shared, not out of an architecture's folder: the icon is the same bytes in both, so
; deduplication always hoists it, and naming an architecture here fails the compile the first time
; the stage is deduplicated.
SetupIconFile={#StageDir}\payload\shared\tray\Assets\rose-mcp.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\tray\RoseMcp.Tray.exe

; Nothing here signs anything, and that is deliberate: signing happens to the finished
; rosemcp-setup.exe, after this has produced it, so whatever a pipeline signs with is that pipeline's
; business and building the installer needs no certificate at all.
;
; The one directive that would drag signing into compilation is SignedUninstaller, because Inno
; embeds the uninstaller inside setup.exe and would have to sign it here. Leaving it off means
; unins000.exe is unsigned, which costs nothing in this install: it runs unelevated, so there is no
; UAC publisher prompt, and it is written locally by setup rather than downloaded, so it carries no
; mark of the web for SmartScreen to check. If a signed uninstaller is ever wanted, ISCC's /S switch
; defines a signing command on the command line, so a non-signtool signer can be given to it.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts"; Flags: unchecked

[Files]
; Inno cannot read the published layout (tools/published-layout.json), so it restates it here, and the
; unit suite holds the two together: every payload line below, its Check: condition, the setup icon
; and every exe this script names under {app} are tested against that file by InstallerLayoutTests.
; A layout change made there and not here fails that test rather than an install.
;
; Everything both architectures build identically, which is most of a payload: the managed assemblies
; are architecture-neutral IL and the two publishes emit the same bytes. Carried once and laid down
; whichever machine this is. The relay's folder is left out here and laid down below, only when the
; relay installed is not this one.
Source: "{#StageDir}\payload\shared\*"; DestDir: "{app}"; Excludes: "\relay"; \
    Flags: recursesubdirs createallsubdirs ignoreversion

; What is genuinely each architecture's own: the apphosts, the assemblies stamped with their RID, and
; the native WindowsAppSDK pieces. Exactly one of these two runs, and neither can collide with the
; shared set -- a file is in shared precisely when every architecture had it identical, and left here
; otherwise.
Source: "{#StageDir}\payload\win-x64\*"; DestDir: "{app}"; Check: IsX64OS; Excludes: "\relay"; \
    Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\payload\win-arm64\*"; DestDir: "{app}"; Check: IsArm64; Excludes: "\relay"; \
    Flags: recursesubdirs createallsubdirs ignoreversion

; The relay a client registers, in a folder an install leaves alone -- with the relays running from it
; -- while the relay there speaks the relay protocol this one does, so those sessions reconnect to the
; new tray by themselves. RelayIsStale is decided once, before any file is laid down, because the
; marker that decides it is itself one of these files.
Source: "{#StageDir}\payload\shared\relay\*"; DestDir: "{app}\relay"; Check: RelayIsStale; \
    Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\payload\win-x64\relay\*"; DestDir: "{app}\relay"; Check: IsX64OS and RelayIsStale; \
    Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\payload\win-arm64\relay\*"; DestDir: "{app}\relay"; Check: IsArm64 and RelayIsStale; \
    Flags: recursesubdirs createallsubdirs ignoreversion

; The debug hosts, which are shared rather than per-architecture. ICorDebug has no cross-architecture
; path, so a host must match the *target* process rather than the broker -- and which targets can
; exist is a fact about the machine. Every allowed machine can execute x64 and x86; only ARM64 can
; execute ARM64. x86 is not a legacy case: it is the default platform of the modern UWP project
; template, so it is what an ordinary new UWP app is built and registered as.
Source: "{#StageDir}\payload\live-app\win-x86\*"; DestDir: "{app}\live-app\win-x86"; \
    Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\payload\live-app\win-x64\*"; DestDir: "{app}\live-app\win-x64"; \
    Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\payload\live-app\win-arm64\*"; DestDir: "{app}\live-app\win-arm64"; Check: IsArm64; \
    Flags: recursesubdirs createallsubdirs ignoreversion

; Installed so the uninstaller can stop what it is about to remove, and extracted to {tmp} as well so
; the same thing can run before the first file is laid down. The layout goes to {tmp} beside it,
; because deciding whether the installed relay stays reads it; removing an install reads nothing.
Source: "{#StageDir}\RoseMcp.Deploy.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\RoseMcp.Deploy.ps1"; Flags: dontcopy
Source: "{#StageDir}\published-layout.json"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\tray\RoseMcp.Tray.exe"; Parameters: "--port {#DefaultPort} --worker ""{app}\RoseMcp.Worker.exe"""
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\tray\RoseMcp.Tray.exe"; Parameters: "--port {#DefaultPort} --worker ""{app}\RoseMcp.Worker.exe"""; Tasks: desktopicon

[Registry]
; The same per-user Run value the tray's own "Start with Windows" toggle writes, so the installer
; setting it and the user clearing it are one switch rather than two that disagree. Quoted, because
; Windows parses this value as a command line and the install path may contain spaces.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "RoseMCP"; ValueData: """{app}\tray\RoseMcp.Tray.exe"""; \
    Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\tray\RoseMcp.Tray.exe"; \
    Parameters: "--port {#DefaultPort} --worker ""{app}\RoseMcp.Worker.exe"""; \
    Description: "Start {#AppName} now"; WorkingDir: "{userdocs}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Logs are not payload, so nothing else removes them and they would outlive the product. Settings are
; deliberately not listed: somebody who uninstalls to reinstall a newer build should not lose what
; they configured, and a single json file is not what anyone is reclaiming space for.
Type: filesandordirs; Name: "{app}\Logs"
; taps\ is deliberately not listed either, for the reason settings are not: a tap dropped in is
; somebody's own file, and the next install looks for it in the same place.
; The relay, which an install that kept it never recorded laying down.
Type: filesandordirs; Name: "{app}\relay"
; The install root itself, once its contents are gone. Inno removes only the files it laid down, and
; a publish leaves generated companions beside them.
Type: dirifempty; Name: "{app}"

[Code]
const
  PowerShellQuiet = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ';

var
  { Whether the relay installed is not this one, so its files are laid down. True until PrepareToInstall
    finds one worth keeping, which a new install never does. }
  RelayStale: Boolean;

function InitializeSetup(): Boolean;
begin
  RelayStale := True;
  Result := True;
end;

function RelayIsStale: Boolean;
begin
  Result := RelayStale;
end;

{ The relay form of the registration, naming the relay this install put down, which is what keeps a
  session connected across the next install. Set here rather than in [Messages], which cannot name
  the install folder. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption :=
      'Setup has finished installing RoseMCP on your computer.' + #13#10#13#10 +
      'Register it with your agent:' + #13#10 +
      '    claude mcp add rose -s user -- "' + ExpandConstant('{app}\relay\RoseMcp.Server.exe') + '"' + #13#10#13#10 +
      'The tray shows this command too.';
end;

{ Runs a snippet against the shared deploy script and returns its exit code. The knowledge of what to
  stop, in what order, and how long to wait for a worker to let go lives in RoseMcp.Deploy.ps1
  because deploy.ps1 and install.ps1 need the same answers. Restating any of it in Pascal would be a
  second set of rules, and only one of them would get fixed. }
function RunDeployScript(const ScriptPath, Snippet: string; var ExitCode: Integer): Boolean;
var
  Command: string;
begin
  Command := PowerShellQuiet + '"' + '. ''' + ScriptPath + '''; ' + Snippet + '"';

  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Command, '',
    SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

{ Prerequisites are reported, never enforced. Somebody installing onto a machine they are about to
  finish setting up is doing a reasonable thing, and the two things RoseMCP needs and cannot carry --
  the .NET SDK and the Windows App Runtime -- are both things a developer machine acquires anyway.
  What matters is that the failure is named here rather than arriving later as thousands of errors
  about System.Object, which reads as a broken solution rather than a missing tool. }
procedure ReportPrerequisites(const ScriptPath: string);
var
  ReportPath, Report: string;
  ExitCode: Integer;
  Lines: TArrayOfString;
  I: Integer;
begin
  ReportPath := ExpandConstant('{tmp}\prerequisites.txt');

  if not RunDeployScript(ScriptPath,
    'Get-PrerequisiteProblem | Set-Content -LiteralPath ''' + ReportPath + '''', ExitCode) then
    Exit;

  if not LoadStringsFromFile(ReportPath, Lines) then
    Exit;

  Report := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Trim(Lines[I]) <> '' then
    begin
      // Always logged, so an unattended install leaves a record of why the thing it installed does
      // not work. The log is the only place a silent run can say anything.
      Log('Prerequisite: ' + Lines[I]);
      Report := Report + Lines[I] + #13#10#13#10;
    end;

  if Report = '' then
    Exit;

  // A dialog would wait for a click that a silent install has nobody to provide, so setup would hang
  // rather than install -- and the machines most likely to be missing a prerequisite are exactly the
  // unattended ones being built from scratch.
  if not WizardSilent then
    MsgBox('RoseMCP will install, but it will not work until these are dealt with:' + #13#10#13#10 +
      Report, mbInformation, MB_OK);
end;

{ Stops whatever is running out of the install root and clears the old payload.

  Both matter and for different reasons. A running exe cannot be overwritten, and the tray, its
  workers and any stdio server an editor started all hold files in this tree -- so without the stop
  the install fails partway through, with the install already unusable. And `publish -o` never
  removes what it does not write, so a file that has left the product would stay in the install
  forever and be loaded by a version that never shipped it; Inno's own file list cannot see those,
  because it never put them there.

  Clearing keeps Logs/, taps/ and settings.json, which live under the same root and are not payload, and the
  relay's folder where the relay in it is this one: its relays are still running, and stay so.
  Whether it was kept is read back afterwards, which is what RelayIsStale answers. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ScriptPath, Root, VerdictPath, Verdict: string;
  ExitCode: Integer;
  Verdicts: TArrayOfString;
begin
  Result := '';
  NeedsRestart := False;

  ExtractTemporaryFile('RoseMcp.Deploy.ps1');
  ExtractTemporaryFile('published-layout.json');
  ScriptPath := ExpandConstant('{tmp}\RoseMcp.Deploy.ps1');
  VerdictPath := ExpandConstant('{tmp}\relay.txt');
  Root := ExpandConstant('{app}');

  ReportPrerequisites(ScriptPath);

  if not DirExists(Root) then
    Exit;

  if not RunDeployScript(ScriptPath,
    'Stop-Install -Root ''' + Root + ''' -KeepCurrentRelay | Out-Null; ' +
    'Clear-InstallPayload -Root ''' + Root + ''' -KeepCurrentRelay; ' +
    '$(if (Test-RelayCurrent -Root ''' + Root + ''') { ''current'' } else { ''stale'' }) | Set-Content -LiteralPath ''' + VerdictPath + '''',
    ExitCode) then
  begin
    Result := 'Could not run the shutdown step. Close the RoseMCP tray and any editor connected to it, then try again.';
    Exit;
  end;

  { A non-zero code here is Stop-Install throwing, and the only thing it throws for is a worker that
    would not exit -- which is exactly the case where continuing overwrites a file somebody still has
    open. Naming the cause beats failing on the first DLL that happens to be locked. }
  if ExitCode <> 0 then
  begin
    Result := 'RoseMCP is still running and would not shut down. Close the tray from its icon, then try again.';
    Exit;
  end;

  // Anything but a clear verdict that the relay stays lays it down again, which is the safe way to be
  // wrong: the relays running were stopped with everything else unless it said current.
  Verdict := '';
  if LoadStringsFromFile(VerdictPath, Verdicts) and (GetArrayLength(Verdicts) > 0) then
    Verdict := Trim(Verdicts[0]);

  RelayStale := Verdict <> 'current';
end;

// The uninstaller has the same problem the installer does: the thing it is removing is running. It
// runs the installed copy of the script rather than a temporary one, because an uninstall does no
// temporary extraction to have put one anywhere else.
//
// Line comments, not a braced block: in the Code section a brace opens a Pascal comment, so naming
// a constant like the temp directory inside one closes it at that constant's own brace and hands
// the rest of the sentence to the compiler as code.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ScriptPath, Root: string;
  ExitCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  Root := ExpandConstant('{app}');
  ScriptPath := Root + '\RoseMcp.Deploy.ps1';

  if FileExists(ScriptPath) then
    RunDeployScript(ScriptPath, 'Stop-Install -Root ''' + Root + ''' | Out-Null', ExitCode);
end;
