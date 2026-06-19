# RDPilot

<p align="center">
  <img src="RDPilot.Client/Assets/screen-alt-2-red-corner-line.svg" alt="RDPilot icon" width="96" height="96">
</p>

RDPilot is a fast Avalonia RDP client backed by FreeRDP. It focuses on responsive remote desktop sessions, dynamic resolution, secure saved profiles, and a clean tabbed interface.

Windows is currently the only supported platform. Linux support is in progress.

## Features

- Dynamic resolution when the client window changes size.
- Low-latency keyboard and mouse input with mouse-move coalescing.
- Multiple simultaneous RDP sessions through tabs.
- Saved connection profiles with passwords stored in the OS credential vault.
- Text clipboard sync between local and remote sessions.
- Per-connection and global quality settings for color depth, font smoothing, wallpaper, themes, animations, full-window drag, compression, bitmap cache, and connection type.

## Install

Download the latest Windows installer from the GitHub Releases page:

```text
https://github.com/ErnieBernie10/RDPilot/releases
```

The installer is per-user and installs to:

```text
%LOCALAPPDATA%\Programs\RDPilot
```

After the winget package is accepted, installation will also be available with:

```powershell
winget install RDPilot.RDPilot
```

## Usage

1. Add a saved connection from the sidebar.
2. Enter the remote PC, username, optional domain, and optional gateway settings.
3. Save the profile. Passwords are stored in the operating system credential store, not in the profile JSON.
4. Click `Connect` to open a new session tab.

Each `Connect` action opens a separate tab. Switching tabs keeps background sessions connected and routes input, resize, and clipboard updates only to the active session.

## Data Storage

Saved connection metadata is stored per user:

- Windows: `%APPDATA%/RDPilot.Client/connections.json`
- macOS: `~/Library/Application Support/RDPilot.Client/connections.json`
- Linux: `$XDG_CONFIG_HOME/RDPilot.Client/connections.json`, or `~/.config/RDPilot.Client/connections.json`

Passwords are stored separately:

- Windows: Credential Manager
- macOS: Keychain
- Linux: Secret Service via `secret-tool`

## Notes

- Clipboard redirection is text-only.
- Audio playback and device redirection are disabled.
- RDPGFX is disabled; RDPilot currently uses the classic GDI rendering path for resize stability.
- Console diagnostics include `[PERF_NATIVE]`, `[PERF_UI]`, `[PERF_INPUT]`, and `[CLIPRDR]` logs.

## Contributing

RDPilot contains two main projects:

- `RDPilot.Client`: .NET/Avalonia desktop client.
- `RDPilot.Wrapper`: native C wrapper around FreeRDP 3 APIs.

### Windows Development

Requirements:

- .NET SDK 10.0 or newer
- CMake
- Visual Studio Build Tools with the MSVC C/C++ toolchain
- vcpkg
- Inno Setup 6 for installer builds

Prepare vcpkg dependencies:

```powershell
scripts/setup-windows-vcpkg.ps1
```

Build and run:

```powershell
scripts/run-windows.ps1
```

Build only:

```powershell
scripts/build-windows.ps1
```

Build a Release installer:

```powershell
scripts/build-installer-windows.ps1 -AppVersion 0.1.0
```

The installer is written to:

```text
artifacts/installer/RDPilot-Setup-<version>-win-x64.exe
```

The default vcpkg location is a sibling of this repository, for example `C:/Users/<you>/Sources/vcpkg` when this repo is `C:/Users/<you>/Sources/rdp-client`. Pass `-VcpkgRoot` to use a different location.

### Linux Development

Linux support is in progress. The notes below are for contributors working on Linux builds and are not a supported end-user installation path yet.

Requirements:

- .NET SDK 10.0 or newer
- CMake
- C compiler toolchain
- pkg-config
- FreeRDP 3 development packages: `freerdp3`, `freerdp-client3`, `winpr3`
- `secret-tool` and a working Secret Service session for password storage

On Arch/CachyOS:

```sh
sudo pacman -S dotnet-sdk cmake gcc pkgconf freerdp
```

Build and run:

```sh
dotnet build RDPilot.slnx
dotnet run --project RDPilot.Client/RDPilot.Client.csproj
```

The .NET project builds the native wrapper with CMake and copies the native library beside the managed output so `DllImport("freerdp_wrapper")` can load it.

### Release Process

Release versions use `major.minor.patch` tags. The tag version is used for the .NET assembly, Inno installer, GitHub Release asset name, and winget package version.

Create a release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The Windows Installer workflow publishes:

```text
RDPilot-Setup-0.1.0-win-x64.exe
RDPilot-Setup-0.1.0-win-x64.exe.sha256
```

Submit or update winget manually with `wingetcreate` after testing the release installer.

New package:

```powershell
wingetcreate new https://github.com/ErnieBernie10/RDPilot/releases/download/v0.1.0/RDPilot-Setup-0.1.0-win-x64.exe
```

Package values:

```text
PackageIdentifier: RDPilot.RDPilot
PackageName: RDPilot
Publisher: RDPilot
PackageVersion: 0.1.0
PackageUrl: https://github.com/ErnieBernie10/RDPilot
PublisherUrl: https://github.com/ErnieBernie10/RDPilot
ShortDescription: Fast Avalonia RDP client backed by FreeRDP.
Moniker: rdpilot
InstallerType: inno
Scope: user
Architecture: x64
```

Update package:

```powershell
wingetcreate update RDPilot.RDPilot --version 0.1.1 --urls https://github.com/ErnieBernie10/RDPilot/releases/download/v0.1.1/RDPilot-Setup-0.1.1-win-x64.exe
```
