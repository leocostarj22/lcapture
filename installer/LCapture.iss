; Inno Setup 6 script. Build via tools\publish.ps1, which passes AppVersion and PublishDir.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{6B1F3C2A-8E4D-4C7B-9A51-2F0D7E3B9C14}
AppName=LCapture
AppVersion={#AppVersion}
AppVerName=LCapture {#AppVersion}
AppPublisher=LCapture
DefaultDirName={autopf}\LCapture
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=LCapture-Setup-{#AppVersion}
SetupIconFile=..\src\LCapture\Assets\app.ico
UninstallDisplayIcon={app}\LCapture.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809+, required by Windows.Media.Capture frame readers.
MinVersion=10.0.17763
; Installs per user without admin rights; the user may still choose "all users".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Self-contained runtime files change between versions; start from a clean folder.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LCapture"; Filename: "{app}\LCapture.exe"
Name: "{autodesktop}\LCapture"; Filename: "{app}\LCapture.exe"; Tasks: desktopicon

[Run]
Filename: "ms-settings:privacy-webcam"; Description: "Abrir as definições de privacidade da câmera (permitir apps de área de trabalho)"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{app}\LCapture.exe"; Description: "{cm:LaunchProgram,LCapture}"; Flags: nowait postinstall skipifsilent
