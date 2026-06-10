# Agent Notes

## Project Overview

This is an experimental RDP client with:

- `RDP.Client`: .NET/Avalonia UI.
- `RDP.Wrapper`: native C shared library wrapping FreeRDP 3 APIs.

The client uses `DllImport("freerdp_wrapper")` to call into `RDP.Wrapper/build/libfreerdp_wrapper.so`.

## Build And Run

Use the solution build; the .NET project configures/builds the native CMake wrapper automatically.

```sh
dotnet build RDP.slnx
dotnet run --project RDP.Client/RDP.Client.csproj
```

System dependencies currently expected on Linux:

- .NET SDK 9+
- CMake
- C compiler
- pkg-config/pkgconf
- FreeRDP 3 development files: `freerdp3`, `freerdp-client3`, `winpr3`

## Native Wrapper Notes

Important FreeRDP 3 details discovered during dynamic-resolution work:

- Raw `freerdp_new()` does not automatically load client channels.
- The wrapper must set `g_instance->LoadChannels = freerdp_client_load_channels` before connect.
- The wrapper must register the static addin provider with `freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0)`.
- `FreeRDP_SupportDisplayControl = TRUE` enables the `disp` dynamic channel through FreeRDP's client channel loader.
- `FreeRDP_DynamicResolutionUpdate = TRUE` is also set for dynamic-resolution behavior.
- Dynamic resolution uses `DispClientContext->SendMonitorLayout` after the `DisplayControlCaps` callback fires.
- Do not send monitor layout updates directly from Avalonia/UI thread. Queue them and send from the RDP thread.
- Resize updates are debounced to avoid corrupting remote graphics streams during drag-resize.

Current native resize behavior:

- Ignores sizes below `640x480` on the Avalonia side.
- Ignores minimized-window resize events.
- Waits for a quiet period before sending `SendMonitorLayout`.
- Resizes local GDI framebuffer after a successful layout send so Avalonia can resize its bitmap.

## Rendering Notes

The wrapper currently disables `FreeRDP_SupportGraphicsPipeline` because RDPGFX/ClearCodec became unstable across dynamic resizes and could disconnect `Microsoft::Windows::RDS::Graphics`.

Classic GDI rendering path is currently preferred for stability while dynamic resolution is being developed.

Do not treat `SurfaceBits` as a full-frame callback. It may represent partial/alternate-surface bitmap data. Full-frame delivery to C# should use the GDI primary framebuffer from `EndPaint`/resize notifications.

The C# frame callback must not post native framebuffer pointers to Avalonia's UI thread. FreeRDP may reallocate/free the framebuffer during resize before the UI thread runs. Copy the frame into managed memory immediately inside `OnFrameReceived`, then render from that managed copy.

## Avalonia Notes

The initial RDP size comes from the measured `ScrollViewer` viewport, not from the `Image` bounds. The `Image` uses `Stretch="None"`, so its bounds can remain at the old remote bitmap size and should not be used as the target resolution.

The startup window is intentionally large (`1440x900`) with `MinWidth="900"` and `MinHeight="600"` so the first connection gets a usable initial desktop size.

## Security Notes

Credentials were previously hardcoded in the view model. Do not reintroduce real credentials into source files.

The native wrapper currently sets `FreeRDP_IgnoreCertificate = TRUE`. This is acceptable only for local experimentation; a proper certificate review/trust flow is needed before packaging or real use.

## Current Verification

At the time these notes were written:

```sh
dotnet build RDP.slnx
```

passes with zero warnings/errors, and a short GUI smoke run starts without native-load errors.
