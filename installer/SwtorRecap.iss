#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif

[Setup]
AppId={{86C572DA-56EC-46D4-9FE3-26031AA6251F}
AppName=SWTOR Recap
AppVersion={#AppVersion}
AppPublisher=SWTOR Recap contributors
AppPublisherURL=https://github.com/ASVPATM/swtor-recap
DefaultDirName={localappdata}\Programs\SWTOR Recap
DefaultGroupName=SWTOR Recap
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=SwtorRecap-{#AppVersion}-Setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\SwtorRecap.exe
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
AppMutex=Local\SwtorRecap.Tracker,Local\SwtorRecap.Desktop
CloseApplications=no
RestartApplications=no
UninstallDisplayName=SWTOR Recap

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SWTOR Recap"; Filename: "{app}\SwtorRecap.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SwtorRecap.exe"; AppUserModelID: "ASVPATM.SwtorRecap"
Name: "{autodesktop}\SWTOR Recap"; Filename: "{app}\SwtorRecap.exe"; WorkingDir: "{app}"; IconFilename: "{app}\SwtorRecap.exe"; AppUserModelID: "ASVPATM.SwtorRecap"; Tasks: desktopicon

[Run]
Filename: "{app}\SwtorRecap.exe"; Description: "Open SWTOR Recap"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SwtorRecap', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\agent\SwtorRecap.Agent.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SwtorRecap');
  { History and original combat logs are never removed. }
end;
