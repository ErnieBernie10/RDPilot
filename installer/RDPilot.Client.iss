; Inno Setup script for the Windows redistributable installer.
; Build through scripts/build-installer-windows.ps1 so SourceDir and OutputDir
; point at the freshly published self-contained win-x64 app folder.

#ifndef SourceDir
  #define SourceDir "..\RDPilot.Client\bin\Release\net10.0\win-x64\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "RDPilot-Setup-0.1.0-win-x64"
#endif

[Setup]
AppId={{8F5E93C6-546B-4E9E-B43A-730353D8EDB5}
AppName=RDPilot
AppVersion={#AppVersion}
AppPublisher=RDPilot
AppPublisherURL=https://github.com/ErnieBernie10/RDPilot
AppSupportURL=https://github.com/ErnieBernie10/RDPilot/issues
AppUpdatesURL=https://github.com/ErnieBernie10/RDPilot/releases
DefaultDirName={localappdata}\Programs\RDPilot
DefaultGroupName=RDPilot
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\RDPilot.Client\Assets\rdpilot.ico
UninstallDisplayIcon={app}\RDPilot.Client.exe,0
VersionInfoCompany=RDPilot
VersionInfoProductName=RDPilot
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RDPilot"; Filename: "{app}\RDPilot.Client.exe"; IconFilename: "{app}\RDPilot.Client.exe"
Name: "{autodesktop}\RDPilot"; Filename: "{app}\RDPilot.Client.exe"; IconFilename: "{app}\RDPilot.Client.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RDPilot.Client.exe"; Description: "Launch RDPilot"; Flags: nowait postinstall skipifsilent
