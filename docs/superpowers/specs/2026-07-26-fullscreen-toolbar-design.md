# Fullscreen Toolbar Design

## Goal

Add true fullscreen RDP viewing without losing access to session tabs or core session actions. The control surface must be unobtrusive, discoverable, and ready for future actions.

## Scope

- Toggle the main window between normal and true OS fullscreen.
- Replace the existing windowed session tab/action row with an extensible toolbar based on the current `SessionTabsView`.
- Keep tabs selectable and preserve existing close, disconnect, and reconnect behaviors.
- Overlay the toolbar over the remote desktop in fullscreen and auto-hide it.
- Support `F11` to toggle fullscreen and `Escape` to leave fullscreen.
- Ensure existing viewport measurement and resize handling react to fullscreen transitions.

## Out Of Scope

- New remote-session actions beyond the fullscreen toggle.
- Persisting fullscreen preference across launches.
- Changing RDP input policy, session lifecycle, or native wrapper behavior.

## Architecture

`MainWindow` owns fullscreen state because it owns the Avalonia `Window` instance and window chrome. `MainWindowViewModel` remains responsible only for session selection and commands.

The existing `SessionTabsView` evolves into the reusable session toolbar. In windowed mode, it occupies its current normal layout position. In fullscreen mode, `MainWindow` places the same control as a top overlay above `RdpViewportView`.

Fullscreen-only visibility logic stays in the view layer. The overlay has a thin top-edge activation area when hidden. Hovering this area reveals the toolbar; it remains visible while the pointer or keyboard focus is within it, then fades out after a brief delay. The hidden activation area remains available so tab selection stays discoverable without moving the pointer across the remote desktop.

## Interaction Design

- The normal toolbar contains session tabs, existing disconnect/reconnect actions, and a fullscreen toggle button.
- Entering fullscreen removes standard window chrome and lets the viewport occupy the window.
- The fullscreen toolbar is top-aligned, compact, translucent, and visually separated from remote content with a subtle background and shadow.
- The toolbar does not reserve viewport layout space while fullscreen; it overlays the desktop.
- The toolbar is initially hidden after fullscreen enters, except while the pointer is already at the top edge.
- A top-edge hover reveals the toolbar. Leaving it begins a short hide delay so users can move between tabs and controls without flicker.
- A keyboard focus inside the toolbar keeps it open.
- `F11` toggles fullscreen. `Escape` exits fullscreen only when fullscreen is active. These keys are handled before RDP key forwarding; all other keyboard input follows existing RDP behavior.
- Selecting a tab, closing a tab, disconnecting, and reconnecting retain their existing command and confirmation behavior.

## State And Data Flow

1. A toolbar button or `F11` requests a fullscreen toggle from `MainWindow`.
2. `MainWindow` sets its Avalonia fullscreen state and updates its visual classes/properties.
3. Entering fullscreen switches the toolbar to overlay presentation and starts hidden; leaving fullscreen restores the normal row presentation.
4. Pointer enter/leave and focus changes control the overlay reveal timer without changing session state.
5. Window layout changes are observed by the existing viewport code, which continues to publish physical-pixel resize updates through the managed scheduler to the native session.

## Error Handling

- The toolbar remains usable with no active session; only commands already guarded by the view model are hidden or disabled.
- If fullscreen cannot be applied by the platform, the window remains usable in its current state and no session state changes.
- Closing the window while fullscreen uses the existing disposal path.

## Testing

- Add UI-sensitive tests for fullscreen toggling and `Escape` behavior using `AvaloniaTestEnvironment`.
- Verify `F11` and the toolbar toggle enter/leave fullscreen without modifying the selected session.
- Verify the fullscreen toolbar remains bound to the same selected-session tab and commands.
- Verify overlay reveal/hide behavior with UI-thread timer scheduling, including focus retention where practical.
- Run the full solution build and relevant client tests.

## Extension Points

The toolbar layout has a dedicated actions region. Future session-scoped commands can be added there without creating another fullscreen-only control surface or duplicating tab logic.
