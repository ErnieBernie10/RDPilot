# Dependency Inventory

This project uses upstream dependencies in the normal build path.

## Current dependency modes

### Windows

- `vcpkg` via the repo `vcpkg.json`
- FreeRDP from the standard upstream `vcpkg` port

Commands:

```powershell
scripts/setup-windows-vcpkg.ps1
scripts/build-windows.ps1
```

### Linux

- System packages: `freerdp3`, `freerdp-client3`, `winpr3`

## Current native dependencies

Required at runtime/build time:

- FreeRDP 3
- FreeRDP client libraries
- WinPR
- CMake
- C compiler / MSVC toolchain
- .NET SDK 10+

Windows `vcpkg.json` currently requests:

- `freerdp[client,ffmpeg]`

Linux also expects:

- `pkg-config` / `pkgconf`
- `secret-tool` for password storage

## Audit findings

Current evidence:

- The native wrapper builds successfully on Windows with the standard upstream `vcpkg` port.
- The project wrapper does not require local FreeRDP patches.
- The old overlay port was removed.

## Maintainability policy

- Default builds should stay on upstream dependencies.
- If a local patch becomes necessary again, document the reason, the upstream issue/PR if any, and the exit plan here.
