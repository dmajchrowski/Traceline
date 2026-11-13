; Traceline setup (Inno Setup 6). Built by build\publish.ps1 -Installer, which passes /DAppVersion=x.y.z,
; /DSourceDir=<folder with the published app> and /O<output folder>.
;
; Installs the program, registers it as a Windows service that starts with the computer, and adds a Start menu
; shortcut that opens Traceline's window. On an update the running service is stopped, updated and started again.
; Recorded data and settings live in C:\ProgramData\Traceline; they are kept on an update, and on an uninstall
; unless the user asks for them to be deleted.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#define AppExe "Traceline.exe"
#define ServiceName "Traceline"
#define DataDir "{commonappdata}\Traceline"

[Setup]
; Never change: upgrades find the existing install by it.
AppId={{6E0B7C1E-52B8-4A0B-9A53-7D3C5B6F4E21}
AppName=Traceline
AppVersion={#AppVersion}
AppVerName=Traceline {#AppVersion}
AppPublisher=Traceline
AppCopyright=Copyright (c) 2026 David Majchrowski
AppComments=Continuous route tracing with latency and packet loss graphs.
; Open source under the MIT License: shown on the License Agreement page, which must be accepted to continue.
LicenseFile=..\LICENSE
; The Windows service must run from a folder non-administrators cannot change, so stay in Program Files.
DefaultDirName={autopf}\Traceline
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=Traceline-Setup-{#AppVersion}
SetupIconFile=..\src\Traceline.App\Assets\traceline.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Traceline
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; [Code] stops the service and closes Traceline's windows itself (PrepareToInstall); this is a fallback for anything else.
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
; Session and workspace files are registered as Traceline's ([Registry]); tells Explorer to pick that up at once.
ChangesAssociations=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "trayatlogon"; Description: "Show the Traceline tray icon when I sign in"; GroupDescription: "Desktop:"
; Off unless ticked: Traceline answers only this computer until "access from other computers" is turned on in its Settings.
Name: "firewall"; Description: "Allow Traceline through Windows Firewall (only needed if you turn on access from other computers in Settings)"; GroupDescription: "Network:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "LICENSE.txt,THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
; Written by build\publish.ps1: the licenses of the .NET runtime and the packages bundled into the exe and the web UI.
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; The web UI's file names change with every build; remove the previous build's files.
Type: filesandordirs; Name: "{app}\wwwroot"
; Builds before 1.3 were under another licence and installed its notice file.
Type: files; Name: "{app}\NOTICE.txt"

[Icons]
; "--tray" finds the running service, opens Traceline in its own window and shows the tray icon.
; At sign-in only the icon is shown ("--minimized"); the window opens from it.
Name: "{autoprograms}\Traceline"; Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Comment: "Traces network routes and graphs latency and packet loss for every hop."
Name: "{autodesktop}\Traceline"; Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Tasks: desktopicon
Name: "{autostartup}\Traceline"; Filename: "{app}\{#AppExe}"; Parameters: "--tray --minimized"; Tasks: trayatlogon

[Registry]
; Traceline's own file types. Double-clicking one opens Traceline's window, which imports the file:
; *.traceline is what one target recorded (a session), *.traceline-workspace is a set of targets and summary screens.
Root: HKA; Subkey: "Software\Classes\.traceline"; ValueType: string; ValueName: ""; ValueData: "Traceline.Session"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Traceline.Session"; ValueType: string; ValueName: ""; ValueData: "Traceline session"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Traceline.Session\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\Traceline.Session\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --tray ""%1"""
Root: HKA; Subkey: "Software\Classes\.traceline-workspace"; ValueType: string; ValueName: ""; ValueData: "Traceline.Workspace"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\Traceline.Workspace"; ValueType: string; ValueName: ""; ValueData: "Traceline workspace"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Traceline.Workspace\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\Traceline.Workspace\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" --tray ""%1"""

[Run]
; Traceline's window does not need administrator rights, so it is opened as the user who started setup.
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Description: "Open Traceline"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Stops and removes the service. Recorded data and settings are kept unless the user asks for them to be deleted ([Code] below).
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Traceline"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewallRule"
; Close a window or tray icon still running from this folder, and wait for the service's process to exit, so the file can be removed.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""$exe = '{app}\{#AppExe}'; function Running {{ Get-CimInstance Win32_Process | Where-Object {{ $_.ExecutablePath -eq $exe } }; Running | Where-Object {{ $_.SessionId -ne 0 } | Invoke-CimMethod -MethodName Terminate | Out-Null; for ($i = 0; $i -lt 50 -and (Running); $i++) {{ Start-Sleep -Milliseconds 200 }"""; Flags: runhidden waituntilterminated; RunOnceId: "CloseRunningCopies"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
var
  ServiceExisted: Boolean;
  DeleteData: Boolean;

function Run(const FileName, Params: String; Show: Integer): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', Show, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

// Remembered once: the service is stopped during the update, and "sc query" answers 1060 when it does not exist.
function InitializeSetup: Boolean;
begin
  ServiceExisted := Run(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', SW_HIDE) <> 1060;
  Result := True;
end;

// Stops the service (it saves what it has in memory first) and closes Traceline's windows and tray icons running from
// the install folder. Restart Manager cannot close windows in other users' sessions, and closes none while the stopped
// service's process is still exiting, so this waits until every copy has exited.
procedure StopServiceAndCloseRunningCopies;
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  StringChangeEx(Exe, '''', '''''', True);
  Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -Command "' +
    '$exe = ''' + Exe + '''; ' +
    '$svc = Get-Service {#ServiceName} -ErrorAction SilentlyContinue; ' +
    'if ($svc -and $svc.Status -ne ''Stopped'') { ' +
    'try { $svc.Stop() } catch { }; try { $svc.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(45)) } catch { } }; ' +
    'function Running { Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $exe } }; ' +
    'Running | Where-Object { $_.SessionId -ne 0 } | Invoke-CimMethod -MethodName Terminate | Out-Null; ' +
    'for ($i = 0; $i -lt 50 -and (Running); $i++) { Start-Sleep -Milliseconds 200 }"', SW_HIDE);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceExisted or FileExists(ExpandConstant('{app}\{#AppExe}')) then
    StopServiceAndCloseRunningCopies;
end;

// After the files are in place: the firewall rule if it was asked for, then the service. "--install-service" registers
// the service, or updates the one that is there, and starts it; what it prints when that fails is shown to the user.
procedure CurStepChanged(CurStep: TSetupStep);
var
  App, Netsh, Details: String;
  Code, I: Integer;
  Output: TExecOutput;
begin
  if CurStep <> ssPostInstall then Exit;
  App := ExpandConstant('{app}\{#AppExe}');
  Netsh := ExpandConstant('{sys}\netsh.exe');

  if WizardIsTaskSelected('firewall') then
  begin
    WizardForm.StatusLabel.Caption := 'Allowing Traceline through Windows Firewall...';
    Run(Netsh, 'advfirewall firewall delete rule name="Traceline"', SW_HIDE);
    // All profiles: networks without a domain are usually left as "Public" by Windows.
    Run(Netsh, 'advfirewall firewall add rule name="Traceline" dir=in action=allow program="' + App + '" enable=yes profile=any', SW_HIDE);
  end;

  WizardForm.StatusLabel.Caption := 'Starting the Traceline service...';
  if not ExecAndCaptureOutput(App, '--install-service', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then
    Code := -1;
  if Code <> 0 then
  begin
    Details := '';
    for I := 0 to GetArrayLength(Output.StdErr) - 1 do
      Details := Details + #13#10 + Output.StdErr[I];
    SuppressibleMsgBox('Traceline is installed, but its Windows service could not be started ' +
      '(code ' + IntToStr(Code) + ').' + Details + #13#10#13#10 +
      'You can still use Traceline from its Start menu shortcut; it then traces only while its window or tray icon is open. ' +
      'To try the service again, run this as administrator:' + #13#10 + '"' + App + '" --install-service', mbError, MB_OK, IDOK);
  end;
end;

// The data folder holds the recorded traces, the targets and alerts, the notification channels with their saved
// passwords and tokens, and the accounts. Keep it unless the user says otherwise; silent uninstalls keep it.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data, Profile: String;
begin
  Data := ExpandConstant('{#DataDir}');
  if (CurUninstallStep = usUninstall) and DirExists(Data) then
    DeleteData := SuppressibleMsgBox('Also delete the recorded data and settings?' + #13#10#13#10 +
      'This removes the recorded traces, the targets and alerts, the notification channels with their saved passwords ' +
      'and tokens, and the accounts from ' + Data + '.' + #13#10#13#10 +
      'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  if (CurUninstallStep = usPostUninstall) and DeleteData then
  begin
    DelTree(Data, True, True, True);
    // The browser profile of Traceline's window (sign-in cookie, cache) of the user who uninstalls.
    Profile := ExpandConstant('{localappdata}\Traceline');
    DelTree(Profile + '\WebView2', True, True, True);
    RemoveDir(Profile);
    // The service's event log source too; when the data is kept it stays, so past events remain readable.
    RegDeleteKeyIncludingSubkeys(HKLM, 'SYSTEM\CurrentControlSet\Services\EventLog\Application\Traceline');
  end;
end;
