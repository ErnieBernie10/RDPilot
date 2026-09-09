# RDPilot — Project Status

> Living status file for AI agents and contributors. Technical build/run rules live in `AGENTS.md`; this file is the *what and why*, maintained with every meaningful change.
>
> Last reviewed: 2026-09-09

## Goal

Fast, open-source RDP client for Windows and Linux (Avalonia + FreeRDP 3): responsive sessions, dynamic resolution, secure saved profiles, clean tabbed UI.

## Board

Managed on the Hermes kanban board `rdpilot` (cross-project portfolio view lives with Arne's Hermes assistant).

## Current Phase

Feature development — multimonitor support is the next milestone. The app is functional and shipping; dependency work (Avalonia, FreeRDP, FluentIcons) is upkeep, not focus.

## Current Outcome (one)

Multimonitor session support: make RDP sessions work correctly across multiple monitors (per-monitor geometry, dynamic resolution updates on multi-screen setups).

## Priorities (max 3)

1. Multimonitor support — session geometry across multiple monitors, correct resize/dynamic-resolution behavior per screen
2. Land the Avalonia 12.1.2 upgrade once Flatpak sources regenerate (reverted to 12.1.1 on Sep 4)
3. Keep FreeRDP overlay current (3.31.0) without breaking the native/managed boundary

## Technical Context for Multimonitor (from code)

- Monitor options already exist in `RDPilot.Client/Models/RdpSessionOptions.cs` (monitor count / selection surface)
- Session view model `RDPilot.Client/ViewModels/RdpSessionViewModel.cs` owns resize/dynamic-resolution flow (`ViewportResolutionUpdateScheduler`, per AGENTS.md coalescing rules)
- Native side: FreeRDP 3 multimonitor flags must go through the `freerdp_wrapper` shim; keep policy in safe C#, handle-based session API (AGENTS.md boundary rules)

## Decisions Log

- 2026-09-09 — Multimonitor support is the next feature milestone (Arne).
- 2026-09-04 — Stay on Avalonia 12.1.1; 12.1.2 breaks Flatpak source regeneration. Revisit when upstream sources are regenerated.
- 2026-08-29 — FreeRDP overlay tracks upstream 3.31.0; source hash pinned per release.
- Standing — Native wrapper stays a thin FreeRDP shim; all policy in safe C# (see AGENTS.md).

## Working Agreement

- Human approves every status change; the assistant (Hermes) proposes, never auto-applies.
- Update this file after any meaningful change: phase shift, priority change, or new decision.
- Keep this file under one screen. Old decisions roll off the bottom (full history is in git).
