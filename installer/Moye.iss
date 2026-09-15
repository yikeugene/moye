#ifndef MoyeVersion
  #error MoyeVersion must be supplied by scripts/publish-installer.ps1.
#endif
#ifndef MoyePayload
  #error MoyePayload must be supplied by scripts/publish-installer.ps1.
#endif
#ifndef MoyeOutput
  #error MoyeOutput must be supplied by scripts/publish-installer.ps1.
#endif

[Setup]
; Keep this identity stable so later installers update the same application.
AppId=net.yikeugene.moye
AppName=Moye
AppVersion={#MoyeVersion}
AppPublisher=Moye contributors
AppPublisherURL=https://github.com/yikeugene/moye
AppSupportURL=https://github.com/yikeugene/moye/issues
AppUpdatesURL=https://github.com/yikeugene/moye/releases/latest
DefaultDirName={localappdata}\Programs\Moye
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
DisableProgramGroupPage=yes
DisableWelcomePage=no
WizardStyle=modern
LicenseFile=..\LICENSE
SetupIconFile=..\src\Moye\Assets\Moye.ico
UninstallDisplayIcon={app}\Moye.exe
VersionInfoVersion={#MoyeVersion}.0
VersionInfoDescription=Moye Setup
OutputDir={#MoyeOutput}
OutputBaseFilename=Moye-{#MoyeVersion}-Setup-win-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
; The payload comes only from the checked, self-contained publish directory.
Source: "{#MoyePayload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; No optional task: every normal installation creates the desktop shortcut.
Name: "{autodesktop}\Moye"; Filename: "{app}\Moye.exe"; WorkingDir: "{app}"; AppUserModelID: "net.yikeugene.moye"
Name: "{autoprograms}\Moye"; Filename: "{app}\Moye.exe"; WorkingDir: "{app}"; AppUserModelID: "net.yikeugene.moye"

[Run]
Filename: "{app}\Moye.exe"; Description: "Launch Moye"; Flags: nowait postinstall skipifsilent

; No UninstallDelete entry: user notebooks live outside {app} and are retained.
