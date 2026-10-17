; Inno Setup 6 script for the Windows installer (MC-Download-Manager-<version>-Setup.exe).
; Built by scripts\build.ps1 when ISCC.exe is available:
;   ISCC /DAppVersion=2.1.0 "/DStage=<...>\build\MC Download Manager" "/DOut=<...>\build" installer.iss
; Keep the UTF-8 BOM: the Turkish messages below need it.
;
; Per-user install (no administrator rights, no UAC prompt) into %LOCALAPPDATA%\Programs\MC Download Manager,
; the folder the zip was meant to be unpacked into, so it also upgrades a copy that was installed from the zip.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Stage
  #define Stage "..\build\MC Download Manager"
#endif
#ifndef Out
  #define Out "..\build"
#endif

#define AppName "MC Download Manager"
#define AppExe "MCDownloadManager.exe"
; Never change this id: it is how Windows (and winget) recognise an installed copy for upgrades.
#define AppGuid "{B6F3A2C4-6E0B-4B0E-9C59-5D1B3C7A2E41}"

[Setup]
AppId={{#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MC Download Manager contributors
; The Windows app's own home: releases, issues and the update check all live there.
AppPublisherURL=https://github.com/miasimbilir/mc-download-manager
AppSupportURL=https://github.com/miasimbilir/mc-download-manager/issues
AppUpdatesURL=https://github.com/miasimbilir/mc-download-manager/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Out}
OutputBaseFilename=MC-Download-Manager-{#AppVersion}-Setup
SetupIconFile=..\src\MCDM.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The app lives in the notification area and turns "close window" into "hide", so Restart Manager cannot
; close it; the [Code] section does (see QuitApp).
CloseApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.RemoveData=Also delete your settings, the download list and the downloaded helper tools (yt-dlp, deno)?%n%nThe files you downloaded are not touched.
tr.RemoveData=Ayarlarınız, indirme listeniz ve indirilen yardımcı araçlar (yt-dlp, deno) da silinsin mi?%n%nİndirdiğiniz dosyalara dokunulmaz.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
; Same name and place as the shortcut the app creates for itself when it was unpacked from the zip.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[UninstallDelete]
; A copy first unpacked from the zip and then upgraded by this installer has folders Setup did not create,
; and Setup only removes folders it created itself.
Type: filesandordirs; Name: "{app}\ChromeExtension"
Type: filesandordirs; Name: "{app}\tools"
Type: dirifempty; Name: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; A silent upgrade (winget upgrade) puts a running app back where it was: in the notification area.
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: RestartHidden
; An update the user started from inside the app (Setup.exe /SILENT /SHOWAPP=1) ends with the new version on screen.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RestartVisible

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  HostName = 'com.mesutcevik.mcdownloadmanager';

var
  WasRunning: Boolean;

function AppRunning(): Boolean;
begin
  // The app holds this mutex for as long as it lives (Program.cs, SingleInstance.MutexName).
  Result := CheckForMutexes('MCDownloadManager.SingleInstance.' + GetUserNameString);
end;

function WaitUntilGone(Seconds: Integer): Boolean;
var
  I: Integer;
begin
  for I := 1 to Seconds * 4 do
  begin
    if not AppRunning() then break;
    Sleep(250);
  end;
  Result := not AppRunning();
end;

{ Closes a running app before its files are replaced or removed. A copy that knows "--quit" leaves the way
  "Quit" in the tray menu does (downloads paused, list saved, tray icon removed); anything else is ended. }
procedure QuitApp(Exe: String; QuitSupported: Boolean);
var
  Code: Integer;
begin
  if not AppRunning() then exit;
  if QuitSupported and FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if WaitUntilGone(3) then exit;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  WaitUntilGone(5);
  Sleep(500);   // the image file stays locked for a moment after the process is ended
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WasRunning := AppRunning();
  // Only a copy put there by this installer is known to understand --quit; an older copy unpacked from
  // the zip would take it for an ordinary second start and show its window.
  QuitApp(ExpandConstant('{app}\{#AppExe}'),
    RegKeyExists(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1'));
  Result := '';
end;

function ShowAfterUpgrade(): Boolean;
begin
  Result := ExpandConstant('{param:SHOWAPP|0}') = '1';
end;

function RestartHidden(): Boolean;
begin
  Result := WizardSilent() and WasRunning and not ShowAfterUpgrade();
end;

function RestartVisible(): Boolean;
begin
  Result := WizardSilent() and ShowAfterUpgrade();
end;

function InitializeUninstall(): Boolean;
begin
  QuitApp(ExpandConstant('{app}\{#AppExe}'), True);
  Result := True;
end;

function PointsHere(Text: String): Boolean;
var
  Plain, Escaped: String;
begin
  Plain := Lowercase(ExpandConstant('{app}\'));
  Escaped := Plain;
  StringChangeEx(Escaped, '\', '\\', True);   // paths inside JSON
  Text := Lowercase(Text);
  Result := (Pos(Plain, Text) > 0) or (Pos(Escaped, Text) > 0);
end;

{ What the app wrote for itself outside its folder: the sign-in entry and the native messaging host
  registrations. Each is removed only if it names this copy. }
procedure RemoveRegistrations();
var
  Vendors: array[0..4] of String;
  I: Integer;
  Value, Manifest: String;
  Content: AnsiString;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, '{#AppName}', Value) and PointsHere(Value) then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, '{#AppName}');

  Vendors[0] := 'Software\Google\Chrome';
  Vendors[1] := 'Software\Microsoft\Edge';
  Vendors[2] := 'Software\BraveSoftware\Brave-Browser';
  Vendors[3] := 'Software\Chromium';
  Vendors[4] := 'Software\Mozilla';
  for I := 0 to 4 do
  begin
    if not RegQueryStringValue(HKEY_CURRENT_USER, Vendors[I] + '\NativeMessagingHosts\' + HostName, '', Manifest) then continue;
    // A manifest that is gone or names this copy; one that names another copy stays. The app writes it as
    // UTF-8 without escaping non-ASCII letters, so a folder such as "Çağrı" must be decoded as UTF-8, not ANSI.
    if (not LoadStringFromFile(Manifest, Content)) or PointsHere(UTF8Decode(Content)) then
    begin
      RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, Vendors[I] + '\NativeMessagingHosts\' + HostName);
      DeleteFile(Manifest);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then exit;
  RemoveRegistrations();
  // Settings and the list stay unless the user says otherwise; a silent uninstall never asks.
  if (not UninstallSilent()) and DirExists(ExpandConstant('{userappdata}\{#AppName}')) then
    if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
end;
