# RDP

Experimental Avalonia RDP client backed by a small native FreeRDP wrapper.

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
