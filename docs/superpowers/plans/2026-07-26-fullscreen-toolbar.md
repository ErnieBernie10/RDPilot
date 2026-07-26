# Fullscreen Toolbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add true fullscreen RDP viewing with an auto-hiding session toolbar that preserves tab selection and existing session actions.

**Architecture:** `MainWindow` owns fullscreen state, window layout, global fullscreen shortcuts, and overlay visibility timing. The existing `SessionTabsView` remains the single session-control surface: in normal mode it reserves a row above the viewport; in fullscreen the same control is moved into an overlay position over the viewport. `RdpViewportView` keeps its existing input routing and physical-pixel resize pipeline.

**Tech Stack:** .NET 10, Avalonia 12, Fluent theme, FluentIcons.Avalonia, CommunityToolkit.Mvvm, xUnit, Avalonia.Headless.

## Global Constraints

- Do not introduce `unsafe` code or change the native wrapper.
- Fullscreen is `WindowState.FullScreen`; leaving it restores the pre-fullscreen window state.
- `MainWindow` owns window policy. `MainWindowViewModel` continues to own only session selection and session commands.
- Keep `F11` and fullscreen `Escape` local to the host window before RDP key forwarding; all other keys retain existing RDP behavior.
- Use the existing RDP viewport resize and managed debounce pipeline; do not add native resize policy.
- Preserve the existing close confirmation, tab selection, reconnect, and disconnect behavior.
- Use `AvaloniaTestEnvironment` and its UI-thread helpers for tests that create controls or windows.

---

## File Structure

- Modify: `RDPilot.Client/Views/MainWindow.axaml` - name the shell regions, host the one session toolbar, and add the fullscreen top-edge reveal area.
- Modify: `RDPilot.Client/Views/MainWindow.axaml.cs` - own fullscreen transitions, keyboard shortcuts, toolbar presentation, and delayed auto-hide behavior.
- Modify: `RDPilot.Client/Views/SessionTabsView.axaml` - add the fullscreen toggle to the existing tabs/actions toolbar without duplicating session controls.
- Modify: `RDPilot.Client/App.axaml` - add compact, translucent fullscreen-toolbar and reveal-zone styles, including the opacity transition.
- Create: `RDPilot.Client.Tests/MainWindowFullscreenTests.cs` - headless UI tests for toggle, restoration, and toolbar command wiring.

### Task 1: Add Fullscreen Toggle And Main Window Transition

**Files:**
- Modify: `RDPilot.Client/Views/SessionTabsView.axaml:7-49`
- Modify: `RDPilot.Client/Views/SessionTabsView.axaml.cs:1-92`
- Modify: `RDPilot.Client/Views/MainWindow.axaml:24-52`
- Modify: `RDPilot.Client/Views/MainWindow.axaml.cs:1-20`
- Modify: `RDPilot.Client/App.axaml:82-109`
- Create: `RDPilot.Client.Tests/MainWindowFullscreenTests.cs`

**Interfaces:**
- Consumes: `TopLevel.GetTopLevel(this) as MainWindow` from the existing toolbar view.
- Produces: `SessionTabsView` raises `FullscreenToggleRequested` when its fullscreen button is clicked.
- Produces: named button `FullscreenToggleButton` for headless UI lookup.
- Produces: `MainWindow` transitions between its captured pre-fullscreen state and `WindowState.FullScreen`.

- [ ] **Step 1: Write the failing toolbar interaction test**

Create `RDPilot.Client.Tests/MainWindowFullscreenTests.cs` with a test that creates a `MainWindow` on the Avalonia UI thread, locates the named fullscreen button, raises its click event, and verifies the window enters fullscreen:

```csharp
[Fact]
public void FullscreenToolbarButton_EntersFullscreen()
{
    AvaloniaTestEnvironment.EnsureInitialized();

    AvaloniaTestEnvironment.RunOnUiThread(() =>
    {
        var window = new MainWindow();
        try
        {
            var button = window.FindControl<Button>("FullscreenToggleButton");
            Assert.NotNull(button);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(WindowState.FullScreen, window.WindowState);
        }
        finally
        {
            window.Close();
        }
    });
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test RDPilot.Client.Tests/RDPilot.Client.Tests.csproj --filter FullyQualifiedName~MainWindowFullscreenTests.FullscreenToolbarButton_EntersFullscreen`

Expected: FAIL because `FullscreenToggleButton` does not exist.

- [ ] **Step 3: Add the button and event contract**

In `SessionTabsView.axaml`, add an icon-only button after the reconnect/disconnect action region. It must be named, show a `FullScreen` Fluent icon, have `ToolTip.Tip="Enter fullscreen"`, use `Classes="IconButton"`, and call `OnFullscreenToggleClicked`:

```xml
<Button x:Name="FullscreenToggleButton"
        Classes="IconButton"
        ToolTip.Tip="Enter fullscreen"
        Click="OnFullscreenToggleClicked">
    <icons:FluentIcon Icon="FullScreen" IconSize="Size16" FontSize="16"/>
</Button>
```

In `SessionTabsView.axaml.cs`, declare and invoke a standard event rather than reaching into window policy from the control:

```csharp
public event EventHandler? FullscreenToggleRequested;

private void OnFullscreenToggleClicked(object? sender, RoutedEventArgs e)
{
    FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
}
```

Do not change the existing close-tab event handler or confirmation dialog.

- [ ] **Step 4: Add a compact fullscreen button style if the existing `IconButton` dimensions do not align with the 28px tab row**

Keep the toolbar compact by adding a dedicated style immediately after `Button.IconButton` only if needed:

```xml
<Style Selector="Button.FullscreenToolbarButton">
    <Setter Property="Width" Value="28"/>
    <Setter Property="MinWidth" Value="28"/>
    <Setter Property="Height" Value="28"/>
    <Setter Property="MinHeight" Value="28"/>
    <Setter Property="Padding" Value="0"/>
</Style>
```

Apply `FullscreenToolbarButton` to the new button rather than altering every existing `IconButton`.

- [ ] **Step 5: Extend the failing test with restoration behavior**

Add a test that begins maximized, clicks the same button twice, and verifies the original state is restored:

```csharp
[Fact]
public void FullscreenToolbarButton_RestoresPreviousWindowState()
{
    AvaloniaTestEnvironment.RunOnUiThread(() =>
    {
        var window = new MainWindow { WindowState = WindowState.Maximized };
        try
        {
            var button = window.FindControl<Button>("FullscreenToggleButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(WindowState.Maximized, window.WindowState);
        }
        finally
        {
            window.Close();
        }
    });
}
```

- [ ] **Step 6: Run the focused tests to verify they fail**

Run: `dotnet test RDPilot.Client.Tests/RDPilot.Client.Tests.csproj --filter FullyQualifiedName~MainWindowFullscreenTests`

Expected: FAIL because `MainWindow` does not subscribe to the toolbar event or manage fullscreen state.

- [ ] **Step 7: Restructure the window markup around a single movable toolbar host**

In `MainWindow.axaml`:

```xml
<Grid x:Name="RootLayout" RowDefinitions="*,Auto">
    <Grid x:Name="ShellGrid" ColumnDefinitions="32,*">
        <views:NavigationRailView x:Name="NavigationRail" Grid.Column="0"/>
        <Grid x:Name="ContentGrid" Grid.Column="1" RowDefinitions="Auto,*" Margin="2" RowSpacing="1">
            <Border x:Name="SessionToolbarHost" VerticalAlignment="Top"/>
            <views:RdpViewportView Grid.Row="1"/>
            <Border x:Name="FullscreenRevealZone"
                    Grid.RowSpan="2"
                    Height="4"
                    VerticalAlignment="Top"
                    IsVisible="False"/>
            <views:ShellOverlayHostView Grid.RowSpan="2" IsOpen="{Binding IsConnectionsPanelOpen}" OverlayWidth="280" ZIndex="10">
                <views:ShellOverlayHostView.OverlayContent>
                    <views:ConnectionsPanelView/>
                </views:ShellOverlayHostView.OverlayContent>
            </views:ShellOverlayHostView>
        </Grid>
    </Grid>
    <Border x:Name="StatusBar" Grid.Row="1" ...>
        <!-- retain the current status content unchanged -->
    </Border>
</Grid>
```

Do not create a second `SessionTabsView`. In the constructor, create one instance, set it as `SessionToolbarHost.Child`, and subscribe once:

```csharp
_sessionToolbar = new SessionTabsView();
_sessionToolbar.FullscreenToggleRequested += OnFullscreenToggleRequested;
SessionToolbarHost.Child = _sessionToolbar;
```

- [ ] **Step 8: Implement fullscreen state and layout updates in `MainWindow`**

Add fields for the toolbar instance, original state, and fullscreen flag:

```csharp
private readonly SessionTabsView _sessionToolbar;
private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
private bool _isFullscreen;
```

Implement one transition method. Capture the current state only when entering fullscreen, and restore it only when exiting:

```csharp
private void SetFullscreen(bool isFullscreen)
{
    if (_isFullscreen == isFullscreen)
    {
        return;
    }

    if (isFullscreen)
    {
        _windowStateBeforeFullscreen = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState;
        WindowState = WindowState.FullScreen;
    }
    else
    {
        WindowState = _windowStateBeforeFullscreen;
    }

    _isFullscreen = isFullscreen;
    ApplyFullscreenLayout();
}
```

`ApplyFullscreenLayout` must hide `NavigationRail` and `StatusBar`, collapse the first `ShellGrid` column and the second `RootLayout` row, make `SessionToolbarHost` span both `ContentGrid` rows and align it to the top, and show `FullscreenRevealZone`. On exit, restore the fixed 32px rail column, status row `Auto`, toolbar host `Grid.RowSpan=1`, and normal content layout. Use `GridLength` rather than hard-coded visual margins in multiple locations.

- [ ] **Step 9: Handle `F11` and `Escape` before RDP input forwarding**

Register `MainWindow`'s tunnel key handler in the constructor before the viewport attaches its own window handler:

```csharp
AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, true);
```

Implement the handler so only local fullscreen keys are consumed:

```csharp
private void OnWindowKeyDown(object? sender, KeyEventArgs e)
{
    if (e.Key == Key.F11)
    {
        SetFullscreen(!_isFullscreen);
        e.Handled = true;
    }
    else if (_isFullscreen && e.Key == Key.Escape)
    {
        SetFullscreen(false);
        e.Handled = true;
    }
}
```

Leave all other keys unhandled so `RdpViewportView.OnKeyDown` preserves the existing routing policy.

- [ ] **Step 10: Add fullscreen styles without changing existing windowed styles**

Add these styles to `App.axaml` after the existing toolbar button styles:

```xml
<Style Selector="Border.FullscreenSessionToolbar">
    <Setter Property="Background" Value="{DynamicResource SystemControlBackgroundChromeMediumBrush}"/>
    <Setter Property="Opacity" Value="0.96"/>
    <Setter Property="Padding" Value="4"/>
    <Setter Property="BoxShadow" Value="0 6 18 0 #66000000"/>
</Style>
<Style Selector="Border.FullscreenRevealZone">
    <Setter Property="Background" Value="Transparent"/>
</Style>
```

When fullscreen is applied, add `FullscreenSessionToolbar` to `SessionToolbarHost.Classes` and `FullscreenRevealZone` to the reveal border classes; remove both when leaving. Use an `Opacity` transition declared directly on the host in `MainWindow.axaml` so it fades rather than appearing abruptly.

- [ ] **Step 11: Run focused tests to verify fullscreen entry and restoration pass**

Run: `dotnet test RDPilot.Client.Tests/RDPilot.Client.Tests.csproj --filter FullyQualifiedName~MainWindowFullscreenTests`

Expected: PASS for button entry and restoration tests.

- [ ] **Step 12: Commit the complete fullscreen transition**

```bash
git add RDPilot.Client/Views/MainWindow.axaml RDPilot.Client/Views/MainWindow.axaml.cs RDPilot.Client/Views/SessionTabsView.axaml RDPilot.Client/Views/SessionTabsView.axaml.cs RDPilot.Client/App.axaml RDPilot.Client.Tests/MainWindowFullscreenTests.cs
```

### Task 2: Add Top-Edge Reveal, Delayed Hiding, And Regression Coverage

**Files:**
- Modify: `RDPilot.Client/Views/MainWindow.axaml:24-52`
- Modify: `RDPilot.Client/Views/MainWindow.axaml.cs`
- Test: `RDPilot.Client.Tests/MainWindowFullscreenTests.cs`

**Interfaces:**
- Consumes: `SetFullscreen(bool)` and named toolbar/reveal elements from Task 1.
- Produces: fullscreen toolbar visibility controlled by pointer entry/exit, keyboard focus, and a UI-thread `DispatcherTimer`.
- Produces: no session selection, command, or RDP input changes while revealing or hiding the local overlay.

- [ ] **Step 1: Write failing visibility and selected-session regression tests**

Add a test that enters fullscreen, verifies the toolbar is hidden after entry, raises pointer entry on `FullscreenRevealZone`, and verifies the toolbar becomes visible and hit-testable. Add a second test that assigns a `MainWindowViewModel` with two test sessions, clicks the second tab before entering fullscreen, then verifies `SelectedSession` remains that second session after entering and leaving fullscreen.

Use the real UI thread and inspect existing controls rather than adding test-only production APIs:

```csharp
var revealZone = window.FindControl<Border>("FullscreenRevealZone")!;
revealZone.RaiseEvent(new PointerEventArgs(InputElement.PointerEnteredEvent));

var toolbarHost = window.FindControl<Border>("SessionToolbarHost")!;
Assert.True(toolbarHost.IsHitTestVisible);
Assert.True(toolbarHost.Opacity > 0);
```

If the installed Avalonia build requires pointer device construction for `PointerEventArgs`, construct the event using the current `MouseDevice` and `PointerPointProperties`; do not replace this interaction test with a production-only test hook.

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test RDPilot.Client.Tests/RDPilot.Client.Tests.csproj --filter FullyQualifiedName~MainWindowFullscreenTests`

Expected: FAIL because the reveal zone has no handlers and the toolbar does not hide after entering fullscreen.

- [ ] **Step 3: Implement local overlay visibility management**

Add a `DispatcherTimer` in `MainWindow` with a 650ms interval. Subscribe it once in the constructor, and stop/dispose it when the window closes. Register pointer and focus handlers for `SessionToolbarHost` plus pointer-entry for `FullscreenRevealZone`:

```csharp
_toolbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
_toolbarHideTimer.Tick += OnToolbarHideTimerTick;
SessionToolbarHost.PointerEntered += OnSessionToolbarPointerEntered;
SessionToolbarHost.PointerExited += OnSessionToolbarPointerExited;
SessionToolbarHost.GotFocus += OnSessionToolbarGotFocus;
SessionToolbarHost.LostFocus += OnSessionToolbarLostFocus;
FullscreenRevealZone.PointerEntered += OnFullscreenRevealZonePointerEntered;
```

Implement the three production operations:

```csharp
private void ShowFullscreenToolbar()
{
    if (!_isFullscreen) return;
    _toolbarHideTimer.Stop();
    SessionToolbarHost.Opacity = 0.96;
    SessionToolbarHost.IsHitTestVisible = true;
}

private void ScheduleFullscreenToolbarHide()
{
    if (_isFullscreen && !SessionToolbarHost.IsKeyboardFocusWithin)
    {
        _toolbarHideTimer.Start();
    }
}

private void HideFullscreenToolbar()
{
    _toolbarHideTimer.Stop();
    if (_isFullscreen && !SessionToolbarHost.IsPointerOver && !SessionToolbarHost.IsKeyboardFocusWithin)
    {
        SessionToolbarHost.Opacity = 0;
        SessionToolbarHost.IsHitTestVisible = false;
    }
}
```

Entering fullscreen must call `HideFullscreenToolbar()` after applying the overlay layout. Leaving fullscreen must stop the timer and restore the host to visible and hit-testable. The focus-lost path must schedule a hide only when the pointer is also outside the host, preventing keyboard tab navigation from closing the toolbar.

- [ ] **Step 4: Verify the viewport remains fully usable after layout changes**

Manually run the client with at least two connections:

```bash
dotnet run --project RDPilot.Client/RDPilot.Client.csproj
```

Verify the following in order:

1. Select each tab in windowed mode and enter fullscreen through the toolbar button.
2. Move to the top edge and switch tabs from the revealed toolbar.
3. Move away and confirm the toolbar fades away without covering remote content.
4. Use `F11` to leave and re-enter fullscreen, then use `Escape` to leave it.
5. Resize the restored window and confirm the remote desktop continues to receive resolution updates.
6. Close a fullscreen tab and confirm the existing close confirmation still appears.

- [ ] **Step 5: Run focused tests to verify reveal and selection preservation pass**

Run: `dotnet test RDPilot.Client.Tests/RDPilot.Client.Tests.csproj --filter FullyQualifiedName~MainWindowFullscreenTests`

Expected: PASS for fullscreen toggling, prior-state restoration, reveal behavior, and selected-session preservation.

- [ ] **Step 6: Run complete verification**

Run: `dotnet build RDPilot.slnx`

Expected: Build succeeds with zero errors.

Run: `dotnet test RDPilot.Client.Tests/RDPilot.Client.Tests.csproj`

Expected: All client tests pass.

- [ ] **Step 7: Commit the reveal behavior and tests**

```bash
git add RDPilot.Client/Views/MainWindow.axaml RDPilot.Client/Views/MainWindow.axaml.cs RDPilot.Client.Tests/MainWindowFullscreenTests.cs
```
