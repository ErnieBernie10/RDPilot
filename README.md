# RDP

Experimental Avalonia RDP client backed by a small native FreeRDP wrapper.

Current focus/features:

- Dynamic resolution through FreeRDP DisplayControl.
- Classic GDI rendering path for resize stability.
- Low-latency input forwarding with coalesced mouse movement.
- Text-only clipboard redirection through FreeRDP `cliprdr`.
- Saved connection profiles with passwords stored in the OS credential vault.
- Multiple simultaneous RDP sessions through tabs.

## System dependencies

The .NET project builds the native wrapper with CMake before compiling the client. A Linux development machine needs:

- .NET SDK 9.0 or newer
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

- .NET SDK 9.0 or newer
- CMake
- Visual Studio Build Tools with the MSVC C/C++ toolchain, or MSYS2 UCRT64
- FreeRDP 3 development files, including `freerdp3`, `freerdp-client3`, and `winpr3`

With MSYS2 UCRT64, install the required packages:

```powershell
C:/msys64/usr/bin/bash.exe -lc "pacman -S --needed mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-freerdp"
```

When MSYS2 is installed at `C:/msys64`, the project automatically uses the UCRT64 `cmake`, `cc`, `ninja`, and `pkg-config` tools for the native wrapper build:

```powershell
dotnet build RDP.slnx
```

For a different MSYS2 install location, pass the root directory:

```powershell
dotnet build RDP.slnx /p:NativeWrapperMsys2Root="D:/tools/msys64"
```

If FreeRDP is provided through a CMake toolchain such as vcpkg, pass it through MSBuild:

```powershell
dotnet build RDP.slnx /p:NativeWrapperCMakeArgs="-DCMAKE_TOOLCHAIN_FILE=C:/vcpkg/scripts/buildsystems/vcpkg.cmake"
```

At runtime, Windows also needs the FreeRDP and WinPR DLLs discoverable by the loader, either beside the .NET output or on `PATH`.
For the default MSYS2 install location, the app automatically adds `C:/msys64/ucrt64/bin` to the DLL search path before loading `freerdp_wrapper`.
Set `RDP_CLIENT_NATIVE_DLL_DIR` to use a different runtime DLL directory.

## Build and run

```sh
dotnet build RDP.slnx
dotnet run --project RDP.Client/RDP.Client.csproj
```

The build creates the native wrapper for the current platform, for example `RDP.Wrapper/build/libfreerdp_wrapper.so` on Linux or `RDP.Wrapper/build/<Configuration>/freerdp_wrapper.dll` with Visual Studio generators on Windows, and copies it beside the .NET output so `DllImport("freerdp_wrapper")` can load it at runtime.

## Saved connections

The app stores saved connection metadata in the current user's profile. Passwords are not written to this JSON file; they are stored through the operating system credential vault.

Profile metadata path:

- Windows: `%APPDATA%/RDP.Client/connections.json`
- macOS: `~/Library/Application Support/RDP.Client/connections.json`
- Linux: `$XDG_CONFIG_HOME/RDP.Client/connections.json`, or `~/.config/RDP.Client/connections.json` when `XDG_CONFIG_HOME` is not set

Password storage:

- Windows: Credential Manager
- macOS: Keychain
- Linux: Secret Service via `secret-tool`

For development migration, `RDP.Client/connection.local.json` is still ignored by Git and may be imported on first run if no saved profiles exist. Do not commit real credentials.

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
