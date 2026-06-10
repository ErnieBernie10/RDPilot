# RDP

Experimental Avalonia RDP client backed by a small native FreeRDP wrapper.

Current focus/features:

- Dynamic resolution through FreeRDP DisplayControl.
- Classic GDI rendering path for resize stability.
- Low-latency input forwarding with coalesced mouse movement.
- Text-only clipboard redirection through FreeRDP `cliprdr`.
- Local-only development connection settings via an ignored JSON file.

## System dependencies

The .NET project builds the native wrapper with CMake before compiling the client. A Linux development machine needs:

- .NET SDK 9.0 or newer
- CMake
- C compiler toolchain
- pkg-config
- FreeRDP 3 development packages, including `freerdp3`, `freerdp-client3`, and `winpr3`

On Arch/CachyOS, the relevant packages are typically:

```sh
sudo pacman -S dotnet-sdk cmake gcc pkgconf freerdp
```

## Build and run

```sh
dotnet build RDP.slnx
dotnet run --project RDP.Client/RDP.Client.csproj
```

The build creates `RDP.Wrapper/build/libfreerdp_wrapper.so` and copies it beside the .NET output so `DllImport("freerdp_wrapper")` can load it at runtime.

## Local connection settings

For development, copy `RDP.Client/connection.local.example.json` to `RDP.Client/connection.local.json` and fill in your RDP settings. The local file is ignored by Git and copied to the app output during build when present.

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
