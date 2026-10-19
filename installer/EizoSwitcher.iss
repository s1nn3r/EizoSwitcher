; EizoSwitcher installer (Inno Setup 6) – built by tools\release.ps1:
;   ISCC.exe /DVersion=1.2.3 /DSourceDir=<package folder> /DOutputDir=<output folder> installer\EizoSwitcher.iss
;
; Per-user installation (no admin rights) to %LOCALAPPDATA%\Programs\EizoSwitcher, so the app can update itself.
; Silent update: EizoSwitcher-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\" + Version + "\package"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\" + Version
#endif

[Setup]
; never change the AppId – Windows identifies the installation by it
AppId={{B7E2C5A1-4E3D-4F7A-9C2B-8D1E6F0A3C59}
AppName=EizoSwitcher
AppVersion={#Version}
AppVerName=EizoSwitcher {#Version}
AppPublisher=Marc Breuer
AppPublisherURL=https://github.com/s1nn3r/EizoSwitcher
AppSupportURL=https://github.com/s1nn3r/EizoSwitcher/issues
AppUpdatesURL=https://github.com/s1nn3r/EizoSwitcher/releases
AppCopyright=© 2026 Marc Breuer
VersionInfoVersion={#Version}
DefaultDirName={autopf}\EizoSwitcher
DefaultGroupName=EizoSwitcher
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=EizoSwitcher-Setup
SetupIconFile=..\src\EizoSwitcher\Assets\EizoSwitcher.ico
UninstallDisplayIcon={app}\EizoSwitcher.exe
UninstallDisplayName=EizoSwitcher
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "nl"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"

[CustomMessages]
NeedsDotNet=EizoSwitcher needs the free Microsoft .NET 10 Desktop Runtime (x64), which is not installed yet.%n%nOpen the download page now? You can install the runtime before or after EizoSwitcher.
de.NeedsDotNet=EizoSwitcher benötigt die kostenlose Microsoft .NET 10 Desktop Runtime (x64), die noch nicht installiert ist.%n%nDownload-Seite jetzt öffnen? Du kannst die Runtime vor oder nach EizoSwitcher installieren.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\EizoSwitcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\eizoswitch.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\EizoSwitcher"; Filename: "{app}\EizoSwitcher.exe"
Name: "{autodesktop}\EizoSwitcher"; Filename: "{app}\EizoSwitcher.exe"; Tasks: desktopicon

[Registry]
; the app writes its autostart entry itself – only make sure it is removed on uninstall
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "EizoSwitcher"; ValueType: none; Flags: uninsdeletevalue

[Run]
Filename: "{app}\EizoSwitcher.exe"; Description: "{cm:LaunchProgram,EizoSwitcher}"; Flags: nowait postinstall skipifsilent
; silent update: start the new version again
Filename: "{app}\EizoSwitcher.exe"; Parameters: "--updated"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; stop only the copy installed here (not e.g. a portable one somewhere else)
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-Process EizoSwitcher -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{app}\*' } | Stop-Process -Force"""; Flags: runhidden; RunOnceId: "StopEizoSwitcher"

[Code]
function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if (not IsDesktopRuntimeInstalled) and (not WizardSilent) then
    if SuppressibleMsgBox(CustomMessage('NeedsDotNet'), mbConfirmation, MB_YESNO, IDYES) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
