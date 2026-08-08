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

## Native/Managed Boundary

Policy lives in safe C#. The native wrapper is a thin FreeRDP host shim with no policy decisions.

**Managed-owned (safe C#, no `unsafe` blocks):**
- Resize debounce/coalescing: `ViewportResolutionUpdateScheduler` (quiet-period + min-interval).
- Pointer-move coalescing/throttling: `PointerMoveScheduler` (latest-only, ~125 Hz min interval).
- Option normalization: `RdpSessionOptions` (color depth, connection type, DPI scale clamping, resolution clamping).
- Clipboard file-list shaping: C# sends paths via native `clear/add/commit` primitives.
- All callback marshaling uses safe `Marshal.PtrToStringUTF8` / `Marshal.ReadIntPtr`.

**Native-owned (FreeRDP mechanics only):**
- FreeRDP event loop, channel setup/callbacks, GDI/RDPGFX framebuffer lifecycle.
- CLIPRDR protocol handling (text, files, bitmap format negotiation).
- RDP-thread input delivery via a simple ordered queue (no coalescing/throttling policy).
- RDP-thread resolution application via `SendMonitorLayout` (no debounce policy).
- Direct FreeRDP settings writes (values are pre-normalized by C#).

Do not reintroduce `unsafe` into `RDPilot.Client.csproj`. Do not reintroduce policy logic (debounce, coalescing, normalization) into the native wrapper.

## Native Wrapper Notes

Important FreeRDP 3 details discovered during dynamic-resolution work:

- Raw `freerdp_new()` does not automatically load client channels.
- The wrapper must set `g_instance->LoadChannels = freerdp_client_load_channels` before connect.
- The wrapper must register the static addin provider with `freerdp_register_addin_provider(freerdp_channels_load_static_addin_entry, 0)`.
- `FreeRDP_SupportDisplayControl = TRUE` enables the `disp` dynamic channel through FreeRDP's client channel loader.
- `FreeRDP_DynamicResolutionUpdate = TRUE` is also set for dynamic-resolution behavior.
- Dynamic resolution uses `DispClientContext->SendMonitorLayout` after the `DisplayControlCaps` callback fires.
- Do not send monitor layout updates directly from Avalonia/UI thread. Queue them and send from the RDP thread.

Current resize behavior (managed-owned policy):

- `ViewportResolutionService` ignores sizes below `640x480` and minimized-window events.
- `ViewportResolutionUpdateScheduler` applies a quiet-period and minimum-interval debounce before forwarding to `RdpSessionViewModel.UpdateResolution`, which normalizes via `RdpSessionOptions` and forwards to native.
- Native queues the latest target size and applies `SendMonitorLayout` on the next RDP loop iteration (no debounce policy).
- Native resizes local GDI framebuffer after a successful layout send so Avalonia can resize its bitmap.

Input behavior (managed-owned policy):

- Do not call FreeRDP input APIs directly from Avalonia/UI event handlers.
- `PointerMoveScheduler` coalesces mouse moves latest-only and throttles to ~125 Hz (8 ms min interval). Pending moves are flushed before button/wheel events so clicks carry a near-current position.
- Native input is a simple ordered FIFO queue drained on the RDP thread. No coalescing or throttling policy in native code.
- The RDP loop is the canonical FreeRDP event-driven form (matches `wfreerdp`/`xfreerdp`): `freerdp_get_event_handles` → `WaitForManyObjects(..., INPUT_LOOP_TIMEOUT_MS=10)` → `freerdp_check_event_handles` (single call). Network data wakes the loop immediately; idle polls are capped at ~100 Hz.
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
- RDPGFX honors the configured color depth, connection type, and visual-effect quality settings. Its local GDI primary buffer remains `PIXEL_FORMAT_BGRX32` regardless of the negotiated color depth.
- Managed option normalization represents `Autodetect` as `NetworkAutoDetect=TRUE` with no server quality hint (`ConnectionType=0`). Sending connection type 7 lets Windows select a high-quality experience that can override explicit visual-effect flags.
- The default `RDPILOT_GFX_CODEC_POLICY` is now `server` (don't filter caps, let the server pick the best codec — matches `wfreerdp`). Forcing `avc420`/`sharp` filters out ClearCodec/Progressive and made tested servers send 2-20 fps with 100-1500 ms frame gaps during drag; `wfreerdp` advertises all caps and the server picks ClearCodec for sharp content. Override with `avc`/`avc420`/`sharp` only for diagnostics.

Do not treat `SurfaceBits` as a full-frame callback. It may represent partial/alternate-surface bitmap data. Full-frame delivery to C# must use the GDI primary framebuffer from `EndPaint`/resize notifications.

Perf logs:

- `[PERF_NATIVE]` reports FreeRDP frame cadence and estimated full-frame throughput.
- `[PERF_UI]` reports managed receive/present/dropped rates, copied MiB/s, queue delay, and approximate input-to-next-render delay.
- `[PERF_LOOP]` reports RDP loop phase timings; under drag the `checkFdsMax` phase should no longer carry a per-frame memcpy.
- `[PERF_INPUT]` appears only when input drops or large mouse-move coalescing batches happen.

## Cursor Notes

FreeRDP does **not** composite the pointer into the framebuffer, so the remote cursor never arrives
through the frame path. It comes from the `rdpPointer` class in `freerdp_wrapper_pointer.c` and is
applied as a real OS cursor on `RdpImage`.

- `register_pointer_class` must run before connect **and** after every `gdi_init` (startup and
  `on_graphics_reset`), next to the update re-hooks. Unregistered, every shape is silently dropped.
- Threading mirrors the frame path: `Pointer_Set` fires a metadata-only `CursorCallback` on the RDP
  thread; the UI thread pulls pixels with `rdp_session_copy_cursor_image` under `cursor_lock`.
- **Cursor bitmaps are `AlphaFormat.Unpremul`** (the framebuffer is `Premul`) — FreeRDP produces
  straight alpha. Wrong here means haloed cursors, not a crash.
- `Pointer_SetPosition` is an intentional no-op; honouring it would warp the physical mouse.

## Clipboard Notes

Clipboard redirection currently supports text and local-to-remote file copy/paste:

- Native wrapper enables `FreeRDP_RedirectClipboard` and handles the static `cliprdr` channel.
- The wrapper sends cliprdr client capabilities on `MonitorReady` before advertising local formats.
- Text uses `CF_UNICODETEXT`.
- Local-to-remote files use `FileGroupDescriptorW` / `FileContents` streaming on `cliprdr` and currently advertise long format names with file paths omitted (matches the working `wfreerdp` behavior used during implementation). C# sends file paths through native `rdp_session_clipboard_clear_local_files` / `rdp_session_clipboard_add_local_file` / `rdp_session_clipboard_commit_local_files` primitives (no managed pointer-array construction).
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

Keyboard handling is scoped to the RDP image but registered on the window's tunnel route with handled events included. This keeps chords such as `Ctrl+Tab` from losing key-up events while still allowing connection text boxes to receive normal typing when focused. This is the ungrabbed path; see "Keyboard Grab" below for the other one.

## Keyboard Grab

There are two mutually exclusive keyboard input paths and they must never both be live:

- **Ungrabbed (default, all platforms):** Avalonia tunnel handlers → `RdpViewportPresenter.HandleKeyDown/Up` → `RdpKeyboardInputMapper.TryMapKey` (Avalonia `Key` → PC/XT set-1 scancode).
- **Grabbed (Windows only):** a `WH_KEYBOARD_LL` hook in `Services/WindowsKeyboardGrab.cs` suppresses every key locally (returns `1`) and raises `KeyIntercepted` → `RdpViewportPresenter.HandleGrabbedKey`. Scancodes come straight from `KBDLLHOOKSTRUCT` via `Services/KeyboardGrabScanCodeMapper.cs`, so this path is layout-independent and does not use `RdpKeyboardInputMapper.TryMapKey`.

`RdpViewportPresenter.SetKeyboardGrabActive` switches between them and flushes the outgoing path's held keys, otherwise modifiers stick on the remote host. `ShouldHandleKeyboardEvent` returns `false` while grabbed so the two paths can never double-send.

Rules:

- The hook callback runs on the Avalonia UI thread. Keep it fast, lock-free and log-free — exceeding `LowLevelHooksTimeout` (~300 ms) makes Windows silently drop the hook. `MainWindow.OnWindowActivated` re-arms it.
- Never leave the hook installed while disengaged; it is global and sees every keystroke on the machine.
- Interception is scoped to `GetForegroundWindow() == our HWND`, and `MainWindow.OnWindowDeactivated` force-releases the grab. With no release hotkey, that is the user's only escape route.
- Grab state is transient per session (`RdpSessionViewModel.IsKeyboardGrabbed`), mirrored to `MainWindowViewModel.IsKeyboardGrabActive` for the toolbar. Do not persist it to `connections.json` or `settings.json`.
- `IKeyboardGrab.IsSupported` is false on Linux (`NullKeyboardGrab`); the toolbar toggle binds `IsEnabled` to it. X11 `XGrabKeyboard` would be a *different* design — it redirects rather than suppresses, so it keeps the Avalonia path as the transport and would need `LWin`/`RWin` added to `RdpKeyboardInputMapper`. Wayland cannot grab at all without `zwp_keyboard_shortcuts_inhibit_manager_v1`.
- `Ctrl+Alt+Del` is unhookable; `RdpSessionViewModel.SendCtrlAltDel` emits the scancode sequence explicitly.

## Test Notes

Use `RDPilot.Client.Tests/AvaloniaTestEnvironment` for any test that touches Avalonia objects or code paths that use `Dispatcher.UIThread`.

- Call `AvaloniaTestEnvironment.EnsureInitialized()` at the start of UI-sensitive tests.
- Use `AvaloniaTestEnvironment.RunOnUiThread(Action)` and `RunOnUiThread<T>(Func<T>)` to create/dispose Avalonia objects and to invoke methods that must run on the real headless UI thread.
- Use `RunPendingDispatcherJobs()` only to flush work that was queued with `Dispatcher.UIThread.Post` from a non-UI thread. This is important for race tests where the production code intentionally posts back to the UI thread.
- For race-style tests, preserve the real ordering. Example: call the callback on the non-UI test thread so it queues UI work, then dispose/mutate state, then call `RunPendingDispatcherJobs()` and assert the queued work was dropped.
- Do not call `Dispatcher.UIThread.RunJobs()` or `Dispatcher.UIThread.MainLoop()` directly in tests. On the Avalonia 12.x headless platform these can hang when used from the wrong thread or outside the headless session flow.
- Do not add test-only hooks, fake `Post` delegates, or production behavior changes just to make tests pass. Fix the test harness to use the real headless UI thread instead.
- Keep pure logic tests free of Avalonia when possible. `PointerMoveScheduler`, `ViewportResolutionUpdateScheduler`, `RdpSessionOptions`, input mappers, and other non-UI helpers should be tested without the headless session.
- For `ManagedFramePresenter` tests specifically, create the presenter on the UI thread and invoke `EnqueueFrame` on the UI thread for normal presentation/coalescing cases. For dispose-before-present races, queue work from the test thread, dispose on the UI thread, then flush posted jobs.
- For `RdpSessionViewModel` callback tests, distinguish between synchronous UI-thread execution and posted UI-thread work. `OnStatusChanged` posts; clipboard text/file callbacks do not. Structure tests accordingly.

## HiDPI Notes

- The host's render scaling is read from `Window.RenderScaling` (a `TopLevel` property). The view (`RdpViewportView`) computes physical pixel dimensions = DIP viewport size × render scaling, and passes them to the native wrapper as `DesktopWidth`/`DesktopHeight`.
- The two scale factors have **different valid ranges** and are normalized separately in `RdpSessionOptions`: `ClampDpiScalePercent` keeps the real percentage for `desktopScaleFactor` (100–500), `ToDeviceScalePercent` snaps `deviceScaleFactor` to the nearest of 100/140/180. Both cross the boundary as explicit `rdp_session_connect` arguments; native does not reinterpret them. Snapping *both* is what makes a 150% display render ~20% too large, and an out-of-range device factor makes the server discard the desktop factor too.
- DPI scale is **locked at connect time** — `rdp_session_update_resolution` does NOT change the DPI factors. The Windows RDP server does not reliably handle mid-session `desktopScaleFactor` changes (UWP processes like the Start Menu don't reflow). Only the pixel resolution changes dynamically when the window moves between monitors with different DPI. This matches mstsc behaviour.
- Because of that lock, the scale must reach `MainWindowViewModel` **before** the first session is created. `RdpViewportPresenter` therefore reports scale and size independently: `UpdateRenderScaling` always fires, `UpdateResolution` only once there is a viewport big enough to measure. Do not fold them back together — the viewport `ScrollViewer` is collapsed until a session is live, so gating the scale on a measurable size strands every first connect at 100%.
- Viewport size is measured from `ViewportHost` (the always-visible `Grid`), not from the collapsed `RdpScrollViewer`. Both report the same size once a session is up.
- `RdpSessionViewModel._renderScaling` owns coordinate scaling (pointer DIP coords × scaling → desktop coords) and `DisplayWidth`/`DisplayHeight`. `ManagedFramePresenter` keeps its own copy but never reads it.
- `Window.LayoutUpdated` is monitored for render-scaling changes (e.g. window dragged to a different-DPI monitor). The handler triggers a resolution update with the new physical pixel size while keeping the connect-time DPI scale.
- Skia (Avalonia's default renderer) **ignores bitmap DPI** — `IBitmap.Dpi` always returns 96 DPI regardless of the `Vector` passed to the `WriteableBitmap` constructor. This is why we use explicit `Image.Width`/`Height` binding + `Stretch="Fill"` instead of relying on bitmap DPI for display sizing.

## Security Notes

Credentials were previously hardcoded in the view model. Do not reintroduce real credentials into source files.

The native wrapper sets `FreeRDP_IgnoreCertificate = FALSE` and provides a `CertificateDecisionCallback` so the host can prompt the user to accept/reject certificates. Trusted fingerprints can be persisted via `ICertificateTrustStore`.

## Flatpak Notes

App ID is `io.github.ErnieBernie10.RDPilot`. Everything lives in `packaging/flatpak/` and is
laid out flat so the directory can be copied straight into the Flathub repo.

- The Flatpak build has **no network**. Every NuGet package is declared in
  `nuget-sources.json`, regenerated by `generate-nuget-sources.sh` (needs Linux + flatpak).
  Regenerate and commit it whenever a `PackageReference` changes, or the build breaks.
- `nuget-sources.json` is also coupled to the SDK patch inside the `dotnet10` extension.
  `microsoft.netcore.app.host.linux-x64` (the apphost for `--self-contained false -r
  linux-x64`) is not bundled with the extension, so its version must match what that SDK
  asks for. An NU1101 on that package after a runtime bump means: regenerate the file.
- The manifest deletes the repo `NuGet.config` before `dotnet publish`; its remote feeds are
  unreachable in the sandbox.
- FreeRDP 3 is **not** in the freedesktop runtime, so the manifest builds it. Keep the
  pkg-config discovery path in `RDPilot.Wrapper/CMakeLists.txt` working — that is what lets
  the wrapper find it in `/app` with no code changes.
- `secret-tool` is not in the runtime either; `libsecret` is built as a module and the app
  gets `--talk-name=org.freedesktop.secrets`. If `SecretStore` ever moves to the Secret
  Service D-Bus API directly, that module can be dropped.
- Keep `finish-args` minimal. No `--filesystem` is currently justified; if remote-to-local
  clipboard file paste lands, route it through the file-chooser portal instead.
- H.264 RDPGFX only decodes when `org.freedesktop.Platform.ffmpeg-full` is mounted at
  `/app/lib/ffmpeg` — the base runtime ships ffmpeg without the patented decoders.
- App icon for packaging is `RDPilot.Client/Assets/rdpilot-app-icon.svg` (plus a 256x256
  PNG). The older `screen-alt-2-red-corner-line.svg` has no background fill and must not be
  used as an app icon.

Traps that cost a CI round-trip each; do not undo these:

- `build-options` must use **`prepend-path`**, not `append-path` (which the dotnet10 README
  suggests). The `dotnet` module's `install.sh` creates `/app/bin/dotnet`, a runtime-only
  copy, and `/app/bin` precedes an appended SDK path — `dotnet publish` then fails with
  "No .NET SDKs were found".
- cmake modules need **`builddir: true`**. flatpak-builder configures in-source otherwise,
  and FreeRDP's `cmake/PreventInSourceBuilds.cmake` aborts.
- FreeRDP options whose default is a computed variable rather than a literal switch
  themselves on and then hard-require a dependency. `WITH_FUSE` is the live example
  (`pkg_check_modules(FUSE3 REQUIRED ...)`); it only powers remote-to-local clipboard file
  paste. Pin them all explicitly.
- The SDK ships CMake 4, which dropped `cmake_minimum_required` below 3.5. cJSON still
  declares 3.0, hence `-DCMAKE_POLICY_VERSION_MINIMUM=3.5` on that module.
- Only files under `packaging/flatpak/` come from the manifest directory. Icons and `LICENSE`
  are read from the **pinned git checkout**, so the pin must be at or after the commit that
  introduced them.
- `appstream-external-screenshot-url` and `appstream-remote-icon-not-mirrored` cannot pass
  outside Flathub's pipeline; the workflow filters exactly those two. See
  `packaging/flatpak/README.md`.

## Current Verification

At the time these notes were written:

```sh
dotnet build RDPilot.slnx
```

passes with zero errors. The C# layer has no `unsafe` blocks (`AllowUnsafeBlocks` is removed from `RDPilot.Client.csproj`). All option normalization, resize debounce, and input coalescing policy is tested via focused managed tests.
