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

Input and latency behavior:

- Do not call FreeRDP input APIs directly from Avalonia/UI event handlers.
- Queue input and process it on the RDP thread.
- Mouse move events are coalesced latest-only to reduce hover/move flood; clicks, wheel, and keys remain ordered queued events.
- The RDP loop currently sleeps briefly (`2ms`) after each iteration to keep input responsive without busy spinning.
- Current low-latency profile uses 16-bit color, compression, bitmap cache, WAN connection type, disabled audio/device redirection, and disabled desktop visual effects.

## Rendering Notes

The wrapper currently disables `FreeRDP_SupportGraphicsPipeline` because RDPGFX/ClearCodec became unstable across dynamic resizes and could disconnect `Microsoft::Windows::RDS::Graphics`.

Classic GDI rendering path is currently preferred for stability while dynamic resolution is being developed.

Do not treat `SurfaceBits` as a full-frame callback. It may represent partial/alternate-surface bitmap data. Full-frame delivery to C# should use the GDI primary framebuffer from `EndPaint`/resize notifications.

The C# frame callback must not post native framebuffer pointers to Avalonia's UI thread. FreeRDP may reallocate/free the framebuffer during resize before the UI thread runs. Copy the frame into managed memory immediately inside `OnFrameReceived`, then render from that managed copy.

The managed renderer keeps only the newest pending frame. If Avalonia is behind, older pending frames are dropped rather than queued, keeping latency lower under activity.

Perf logs:

- `[PERF_NATIVE]` reports FreeRDP frame cadence and estimated full-frame throughput.
- `[PERF_UI]` reports managed receive/render rates, dropped frames, UI queue delay, and approximate input-to-next-render delay.
- `[PERF_INPUT]` appears only when input drops or large mouse-move coalescing batches happen.

## Clipboard Notes

Clipboard redirection is text-only at the moment:

- Native wrapper enables `FreeRDP_RedirectClipboard` and handles the static `cliprdr` channel.
- The wrapper sends cliprdr client capabilities on `MonitorReady` before advertising local formats.
- Only `CF_UNICODETEXT` is advertised/requested/responded to.
- C# owns interaction with Avalonia's `TopLevel.Clipboard`; native calls back with remote UTF-8 text and C# writes it to the local OS clipboard.
- Local-to-remote clipboard uses an Avalonia-side polling timer and caches the latest non-empty text in native code so the RDP thread can answer remote paste requests synchronously.
- Empty local clipboard reads are ignored because Avalonia/platform clipboard reads may transiently return empty values and should not clear the remote clipboard offer.
- File, bitmap, HTML, and custom clipboard formats are not implemented yet.

## Avalonia Notes

The initial RDP size comes from the measured `ScrollViewer` viewport, not from the `Image` bounds. The `Image` uses `Stretch="None"`, so its bounds can remain at the old remote bitmap size and should not be used as the target resolution.

The startup window is intentionally large (`1440x900`) with `MinWidth="900"` and `MinHeight="600"` so the first connection gets a usable initial desktop size.

Keyboard handling is scoped to the RDP image but registered on the window's tunnel route with handled events included. This keeps chords such as `Ctrl+Tab` from losing key-up events while still allowing connection text boxes to receive normal typing when focused.

## Security Notes

Credentials were previously hardcoded in the view model. Do not reintroduce real credentials into source files.

The native wrapper currently sets `FreeRDP_IgnoreCertificate = TRUE`. This is acceptable only for local experimentation; a proper certificate review/trust flow is needed before packaging or real use.

## Current Verification

At the time these notes were written:

```sh
dotnet build RDP.slnx
```

passes with zero warnings/errors, and a short GUI smoke run starts without native-load errors.
