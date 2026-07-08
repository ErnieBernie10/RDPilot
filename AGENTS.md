BE BRIEF.

# Agent Notes

## Project Overview

This is an RDP client with:

- `RDPilot.Client`: .NET/Avalonia UI.
- `RDPilot.Wrapper`: native C shared library wrapping FreeRDP 3 APIs.

The client uses `DllImport("freerdp_wrapper")` to load the native wrapper copied beside the .NET output. Linux builds produce `RDPilot.Wrapper/build/native/libfreerdp_wrapper.so`.

Native sessions are handle-based. `rdp_session_connect` returns an opaque `rdp_session*`; resize, input, clipboard, disconnect, and free calls must use that handle. Do not reintroduce singleton native state for connection/session data.

## Build And Run

Use the solution build; the .NET project configures/builds the native CMake wrapper automatically.

```sh
dotnet build RDPilot.slnx
dotnet run --project RDPilot.Client/RDPilot.Client.csproj
```

System dependencies currently expected on Linux:

- .NET SDK 10+
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
- The coalesced pending mouse move is throttled to ~125 Hz (`MIN_MOVE_SEND_INTERVAL_MS = 8`). RDPGFX frame scheduling on the server gates on input quiescence; the previous busy-poll loop spammed moves at ~500 Hz and made the server pace frames down to 1-12 fps during drag. The pending move stays updated latest-only so the server always sees the most recent position while staying within the rate budget (matches mstsc/wfreerdp).
- The RDP loop is now the canonical FreeRDP event-driven form (matches `wfreerdp`/`xfreerdp`): `freerdp_get_event_handles` → `WaitForMultipleObjects(..., INPUT_LOOP_TIMEOUT_MS=10)` → `freerdp_check_event_handles` (single call, which itself invokes `freerdp_check_fds` + `freerdp_channels_check_fds` + error-event check). No `Sleep(2)`, no redundant per-handle `WaitForSingleObject(0)`, no standalone `freerdp_check_fds` call. Network data wakes the loop immediately; idle polls are capped at ~100 Hz.
- Current low-latency profile uses 16-bit color, compression, bitmap cache, WAN connection type, disabled audio/device redirection, and disabled desktop visual effects.

## Rendering Notes

The default rendering mode is now `gfx-gdi` (RDPGFX with FreeRDP's standard `gdi_graphics_pipeline_init`, aligned with `wfreerdp`). The custom `rdpgfx-surface` path has been removed; only `gfx-gdi` and the legacy `classic-gdi` mode remain. `classic-gdi` is kept for fallback only and is not the production path. Set `RDPILOT_RENDERING_MODE=classic-gdi` to force the legacy path.

Shared-buffer presentation (mirrors `wfreerdp.exe`):

- `FreeRDP_SoftwareGdi` is now `TRUE` in gfx mode and the wrapper uses FreeRDP's standard `gdi_graphics_pipeline_init` (no `gdi_graphics_pipeline_init_ex`, no `SurfaceCommand`/`EndFrame`/`UpdateSurfaceArea` overrides, no surface-renderer dirty tracking).
- FreeRDP decodes RDPEGFX surfaces directly into the GDI primary buffer (`gdi->primary_buffer`) using `PIXEL_FORMAT_BGRX32`.
- `on_end_paint` (native, RDP thread) unions `hwnd->cinvalid[]` into a single extents rect, stores it into the native pending slot under `frame_lock`, and invokes the C# `FrameCallback` with the *live* `gdi->primary_buffer` pointer plus the dirty extents. **No pixel copy happens on the RDP thread.** This mirrors `wf_end_paint`, which only calls `InvalidateRect`.
- `rdp_session_present` (UI thread) atomically copies the dirty rect from the live `gdi->primary_buffer` into a caller-provided destination buffer **under `frame_lock`**, which is also held across `gdi_resize`. This prevents the primary buffer from being freed/reallocated mid-copy (unlike `wfreerdp`, which runs decode and present on a single message thread, our RDP thread and UI thread are separate). The function returns the dirty rect or signals a resize race so the caller can recreate its bitmap.
- `resize_local_framebuffer` holds `frame_lock` across `gdi_resize`, then resets the pending dirty rect to full screen and notifies C# via the `FrameCallback`.
- The C# `FrameCallback` (`OnFrameReceived`) does not copy bytes; it records the dirty rect/dims and posts a UI-thread present. `ManagedFramePresenter.Present` runs on the UI thread, calls `rdp_session_present` (which does the locked copy natively), recreates the `WriteableBitmap` on resize, and triggers one `InvalidateVisual` per logical desktop frame.
- The managed renderer keeps only the newest pending frame geometry (last-write-wins with dirty-rect union across frames that arrive while a present is already queued). Intermediate frames are intentionally dropped to bound latency under drag, exactly like Windows coalescing `InvalidateRect` calls.

RDPGFX investigation notes (historical context):

- If switching back from the old experimental branch/stash to a clean tree, delete `RDPilot.Wrapper/build/vcpkg-msvc` if CMake complains about missing root `vcpkg.json`; the stale CMake cache may still point at the manifest from the experimental branch.
- `wfreerdp.exe` was used for historical Windows diagnostics. If you use it again, ensure the matching FreeRDP runtime DLLs are beside the executable.
- `wfreerdp` uses `PIXEL_FORMAT_BGRX32` local framebuffer and FreeRDP's standard Windows GDI presentation (`InvalidateRect`/`BitBlt`) with `gdi_graphics_pipeline_init`. RDPilot now matches that path on the native side and only deviates on the present primitive (Avalonia `WriteableBitmap` instead of `BitBlt`).
- FreeRDP with `ffmpeg` enabled defines `WITH_GFX_H264`; without the manifest/ffmpeg build it does not advertise/decode RDPGFX H.264/AVC.
- Even with `WITH_GFX_H264`, tested servers confirmed `RDPGFX_CAPVERSION_81` with AVC420 flag `0x00000002` but still sent `avc=0`; actual updates were ClearCodec/progressive.
- The smoother server was smoother because it delivered far steadier ClearCodec updates, often around 26-31 FPS. The slower server delivered far fewer completed frames despite similar caps.
- Frame acknowledgements are required for these servers. Disabling RDPGFX frame ack froze the session. QoE ack did not resolve pacing issues.
- For RDPGFX, force 32-bit color and use LAN/high-quality connection settings; the legacy low-latency classic-GDI profile uses 16-bit/WAN/disabled visuals and may influence server graphics choices.
- The default `RDPILOT_GFX_CODEC_POLICY` is now `server` (don't filter caps, let the server pick the best codec — matches `wfreerdp`). Forcing `avc420`/`sharp` filters out ClearCodec/Progressive and made tested servers send 2-20 fps with 100-1500 ms frame gaps during drag; `wfreerdp` advertises all caps and the server picks ClearCodec for sharp content. Override with `avc`/`avc420`/`sharp` only for diagnostics.

Do not treat `SurfaceBits` as a full-frame callback. It may represent partial/alternate-surface bitmap data. Full-frame delivery to C# must use the GDI primary framebuffer from `EndPaint`/resize notifications.

Perf logs:

- `[PERF_NATIVE]` reports FreeRDP frame cadence and estimated full-frame throughput.
- `[PERF_UI]` reports managed receive/present/dropped rates, copied MiB/s, queue delay, and approximate input-to-next-render delay.
- `[PERF_LOOP]` reports RDP loop phase timings; under drag the `checkFdsMax` phase should no longer carry a per-frame memcpy.
- `[PERF_INPUT]` appears only when input drops or large mouse-move coalescing batches happen.

## Clipboard Notes

Clipboard redirection currently supports text and local-to-remote file copy/paste:

- Native wrapper enables `FreeRDP_RedirectClipboard` and handles the static `cliprdr` channel.
- The wrapper sends cliprdr client capabilities on `MonitorReady` before advertising local formats.
- Text uses `CF_UNICODETEXT`.
- Local-to-remote files use `FileGroupDescriptorW` / `FileContents` streaming on `cliprdr` and currently advertise long format names with file paths omitted (matches the working `wfreerdp` behavior used during implementation).
- C# owns interaction with Avalonia's `TopLevel.Clipboard`; native calls back with remote UTF-8 text and C# writes it to the local OS clipboard.
- Local-to-remote clipboard uses an Avalonia-side polling timer. Text is cached as the latest non-empty string in native code so the RDP thread can answer remote paste requests synchronously; file offers are rebuilt from the current Avalonia clipboard file list.
- Empty local clipboard reads are ignored because Avalonia/platform clipboard reads may transiently return empty values and should not clear the remote clipboard offer.
- Remote-to-local file paste is not implemented yet.
- Bitmap, HTML, and custom clipboard formats are not implemented yet.

## Avalonia Notes

Connection management UI:

- `MainWindow` is a management shell with a saved-connections sidebar, selected profile summary, status bar, and RDP viewport.
- Add/edit runs through `ConnectionEditorWindow`; keep password fields out of the main shell.
- `MainWindowViewModel` owns saved profile loading, selected connection state, tab collection, and connect/disconnect/close-tab commands.
- `RdpSessionViewModel` owns one native session handle, frame coalescing, bitmap rendering state, resize, input, and per-session callbacks.
- `Connect` creates a new tab/session even when another tab for the same saved connection already exists.
- Switching tabs must not disconnect background sessions. Input, resize, and local clipboard updates should route only to `SelectedSession`.
- `ConnectionStore` stores non-secret metadata in per-user `connections.json` via `AppDataPaths`.
- `connection.local.json` is only a local development/import convenience and must remain ignored by Git.

Credential storage:

- Do not store passwords in `connections.json` or source files.
- `SecretStore.CreateDefault()` selects Windows Credential Manager, macOS Keychain, or Linux Secret Service via `secret-tool`.
- Linux password save/load requires a working Secret Service session and `secret-tool`; do not silently fall back to plaintext.
- Secret keys are derived from the saved connection ID with `SecretStore.PasswordKey` and `SecretStore.GatewayPasswordKey`.

The initial RDP size comes from the measured `ScrollViewer` viewport (in DIPs) multiplied by `Window.RenderScaling` to get physical pixels. The `Image` uses `Stretch="Fill"` with explicit `Width`/`Height` bound to `SelectedSession.DisplayWidth`/`DisplayHeight` (= framebuffer pixels / render scaling), so the bitmap maps 1:1 to physical display pixels → sharp on both 100% and scaled displays.

The startup window is intentionally large (`1440x900`) with `MinWidth="900"` and `MinHeight="600"` so the first connection gets a usable initial desktop size.

Keyboard handling is scoped to the RDP image but registered on the window's tunnel route with handled events included. This keeps chords such as `Ctrl+Tab` from losing key-up events while still allowing connection text boxes to receive normal typing when focused.

## HiDPI Notes

- The host's render scaling is read from `Window.RenderScaling` (a `TopLevel` property). The view (`RdpViewportView`) computes physical pixel dimensions = DIP viewport size × render scaling, and passes them to the native wrapper as `DesktopWidth`/`DesktopHeight`.
- DPI scale percentage is passed to `rdp_session_connect` at connect time via `dpi_scale_percent`. The native wrapper clamps it to the RDP-valid steps (100/140/180) and sets `FreeRDP_DesktopScaleFactor`/`FreeRDP_DeviceScaleFactor` accordingly. This makes the remote session DPI-aware so text/menus render at the right size on scaled displays.
- DPI scale is **locked at connect time** — `rdp_session_update_resolution` does NOT change the DPI factors. The Windows RDP server does not reliably handle mid-session `desktopScaleFactor` changes (UWP processes like the Start Menu don't reflow). Only the pixel resolution changes dynamically when the window moves between monitors with different DPI. This matches mstsc behaviour.
- `ManagedFramePresenter` stores `_renderScaling` for coordinate scaling (pointer events multiply DIP coords by `_renderScaling` to get desktop coords) and for `DisplayWidth`/`DisplayHeight` computation (so the Avalonia `Image` is sized correctly in DIPs).
- `Window.LayoutUpdated` is monitored for render-scaling changes (e.g. window dragged to a different-DPI monitor). The handler triggers a resolution update with the new physical pixel size while keeping the connect-time DPI scale.
- Skia (Avalonia's default renderer) **ignores bitmap DPI** — `IBitmap.Dpi` always returns 96 DPI regardless of the `Vector` passed to the `WriteableBitmap` constructor. This is why we use explicit `Image.Width`/`Height` binding + `Stretch="Fill"` instead of relying on bitmap DPI for display sizing.

## Security Notes

Credentials were previously hardcoded in the view model. Do not reintroduce real credentials into source files.

The native wrapper sets `FreeRDP_IgnoreCertificate = FALSE` and provides a `CertificateDecisionCallback` so the host can prompt the user to accept/reject certificates. Trusted fingerprints can be persisted via `ICertificateTrustStore`.

## Current Verification

At the time these notes were written:

```sh
dotnet build RDPilot.slnx
```

passes with zero warnings/errors, and a short GUI smoke run starts without native-load errors.
