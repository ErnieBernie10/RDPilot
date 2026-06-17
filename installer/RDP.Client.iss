; Inno Setup script for the Windows redistributable installer.
; Build through scripts/build-installer-windows.ps1 so SourceDir and OutputDir
; point at the freshly published self-contained win-x64 app folder.

#ifndef SourceDir
  #define SourceDir "..\RDP.Client\bin\Release\net9.0\win-x64\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{8F5E93C6-546B-4E9E-B43A-730353D8EDB5}
AppName=RDP Client
AppVersion={#AppVersion}
AppPublisher=RDP Client
DefaultDirName={localappdata}\Programs\RDP Client
DefaultGroupName=RDP Client
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=RDP.Client-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\RDP.Client\Assets\avalonia-logo.ico
UninstallDisplayIcon={app}\RDP.Client.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RDP Client"; Filename: "{app}\RDP.Client.exe"
Name: "{autodesktop}\RDP Client"; Filename: "{app}\RDP.Client.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RDP.Client.exe"; Description: "Launch RDP Client"; Flags: nowait postinstall skipifsilent
