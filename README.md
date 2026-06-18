# RDPilot

<p align="center">
  <img src="RDPilot.Client/Assets/screen-alt-2-red-corner-line.svg" alt="RDPilot icon" width="96" height="96">
</p>

RDPilot is an experimental Avalonia RDP client backed by a small native FreeRDP wrapper.

Current focus/features:

- Dynamic resolution through FreeRDP DisplayControl.
- Classic GDI rendering path for resize stability.
- Low-latency input forwarding with coalesced mouse movement.
- Text-only clipboard redirection through FreeRDP `cliprdr`.
- Saved connection profiles with passwords stored in the OS credential vault.
- Multiple simultaneous RDP sessions through tabs.

## System dependencies

The .NET project builds the native wrapper with CMake before compiling the client. A Linux development machine needs:

- .NET SDK 10.0 or newer
- CMake
- C compiler toolchain
- pkg-config
- FreeRDP 3 development packages, including `freerdp3`, `freerdp-client3`, and `winpr3`
- `secret-tool`/Secret Service for saving passwords on Linux

On Arch/CachyOS, the relevant packages are typically:

```sh
sudo pacman -S dotnet-sdk cmake gcc pkgconf freerdp
```

For Linux password storage, install the package that provides `secret-tool` and make sure a Secret Service keyring is running. On many GNOME-based systems this comes from `libsecret` and `gnome-keyring`.

A Windows development machine needs:

- .NET SDK 10.0 or newer
- CMake
- Visual Studio Build Tools with the MSVC C/C++ toolchain
- vcpkg
- Inno Setup 6 for building the redistributable installer

Preferred Windows setup for packaging:

```powershell
scripts/setup-windows-vcpkg.ps1
```

This installs `freerdp[client]:x64-windows` through the repository's vcpkg overlay port. The overlay matches the upstream vcpkg FreeRDP port but enables FreeRDP/WinPR's internal RC4 implementation, which is required for RDP licensing with OpenSSL 3.

Build and run the app for local development:

```powershell
scripts/run-windows.ps1
```

Build without running:

```powershell
scripts/build-windows.ps1
```

Build the Release app:

```powershell
scripts/build-windows.ps1 -Configuration Release
```

Publish the redistributable self-contained Windows app folder:

```powershell
scripts/publish-windows.ps1
```

Build the redistributable Windows installer:

```powershell
scripts/build-installer-windows.ps1
```

The installer is written to `artifacts/installer/RDPilot.Client-Setup-win-x64.exe`. It first publishes the self-contained `win-x64` app, verifies that `freerdp_wrapper.dll` and FreeRDP/WinPR dependency DLLs are present, then packages the complete publish folder recursively.

The default vcpkg location is a sibling of this repository, for example `C:/Users/<you>/Sources/vcpkg` when the repo is `C:/Users/<you>/Sources/rdp-client`. Pass `-VcpkgRoot` to the setup/build/publish scripts to use a different location.

The vcpkg toolchain performs app-local deployment for normal native DLL imports, and the project copies the native wrapper and deployed FreeRDP/WinPR dependency DLLs into the publish folder.

At runtime, Windows loads FreeRDP and WinPR DLLs from the app output directory. Set `RDPILOT_NATIVE_DLL_DIR` only when intentionally testing a different native DLL directory.

## Build and run

```sh
dotnet build RDPilot.slnx
dotnet run --project RDPilot.Client/RDPilot.Client.csproj
```

The build creates the native wrapper for the current platform, for example `RDPilot.Wrapper/build/libfreerdp_wrapper.so` on Linux or `RDPilot.Wrapper/build/<Configuration>/freerdp_wrapper.dll` with Visual Studio generators on Windows, and copies it beside the .NET output so `DllImport("freerdp_wrapper")` can load it at runtime.

## Saved connections

The app stores saved connection metadata in the current user's profile. Passwords are not written to this JSON file; they are stored through the operating system credential vault.

Profile metadata path:

- Windows: `%APPDATA%/RDPilot.Client/connections.json`
- macOS: `~/Library/Application Support/RDPilot.Client/connections.json`
- Linux: `$XDG_CONFIG_HOME/RDPilot.Client/connections.json`, or `~/.config/RDPilot.Client/connections.json` when `XDG_CONFIG_HOME` is not set

Password storage:

- Windows: Credential Manager
- macOS: Keychain
- Linux: Secret Service via `secret-tool`

For development migration, `RDPilot.Client/connection.local.json` is still ignored by Git and may be imported on first run if no saved profiles exist. Do not commit real credentials.

## Runtime notes

The client currently optimizes for responsiveness over visual quality:

- RDP color depth is set to 16-bit.
- Wallpaper, themes, menu animations, cursor shadow, and full-window drag are disabled.
- Audio playback and device redirection are disabled.
- RDPGFX is disabled; the wrapper uses the classic GDI path.

Console logs include lightweight diagnostics:

- `[PERF_NATIVE]`: FreeRDP frame cadence and estimated full-frame throughput.
- `[PERF_UI]`: managed receive/render rate, dropped/coalesced frame data, UI queue delay, and approximate input-to-next-frame delay.
- `[PERF_INPUT]`: queued input drops or large mouse-move coalescing batches.
- `[CLIPRDR]`: clipboard channel, format negotiation, and text transfer events.

Clipboard redirection is currently text-only using `CF_UNICODETEXT`. The Avalonia side polls the local clipboard periodically because there is no cross-platform clipboard-changed event in use here. Empty clipboard reads are ignored to avoid clearing the remote clipboard offer after transient platform reads.

## Session tabs

Each click of `Connect` opens a new RDP session tab for the selected saved connection. Switching tabs changes the visible framebuffer and routes input, resize, and local clipboard updates to the active session without disconnecting background sessions. Closing a tab disconnects and frees that session.
