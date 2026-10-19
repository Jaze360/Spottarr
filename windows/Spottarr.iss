#ifndef BundleDir
  #error BundleDir must point to the published Windows bundle
#endif
[Setup]
AppId={{36BE54AF-452A-42C1-A095-FD42584AFDA1}
AppName=Spottarr Windows Configurator (Preview)
AppVersion=0.1.0
DefaultDirName={localappdata}\Programs\Spottarr
DefaultGroupName=Spottarr
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=Spottarr-Windows-0.1.0-preview-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#BundleDir}\LICENSE
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\Spottarr.Windows.exe
[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Spottarr configureren"; Filename: "{app}\Spottarr.Windows.exe"
[Run]
Filename: "{app}\Spottarr.Windows.exe"; Description: "Spottarr configureren"; Flags: nowait postinstall skipifsilent
[UninstallRun]
Filename: "{app}\Spottarr.Windows.exe"; Parameters: "--remove-startup"; Flags: runhidden waituntilterminated
; User configuration and SQLite under LocalAppData\Spottarr are intentionally preserved.
