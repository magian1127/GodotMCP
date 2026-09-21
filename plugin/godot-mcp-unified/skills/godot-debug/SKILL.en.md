---
name: godot-debug
description: Diagnose and fix reported Godot errors, crashes, broken scenes, invalid scripts, debugger failures, or runtime regressions through Godot MCP Unified. Use when something already fails or behaves incorrectly; use godot-control for ordinary feature work and godot-playtest for planned gameplay verification.
---

Language: English | [中文](SKILL.md)

# Godot Debug

Run an evidence-first repair loop.

1. Identify the exact project, scene, reproduction action, expected behavior, and observed failure. Do not theorize from a stale registry entry or one log line.
2. Collect both channels before editing:
   - script_check for a changed file and lsp_diagnostics(scope="project") for cross-file GDScript errors.
   - log_read(channel="editor") for editor/import warnings.
   - log_read(channel="runtime") for running-game output and crash context.
   - Load the debugger group for breakpoints, pause/continue, and debug_inspect.
3. Inspect the implicated scene tree, node properties, script, signals, and resources. Distinguish parse/import errors, editor state, and runtime state.
4. Make the smallest correction through typed scene/script/resource tools. Preserve unrelated edits and avoid execute_code as a speculative probe.
5. Repeat the original reproduction. Re-run diagnostics and compare concrete state/log values.

Track debugger, LSP, and other on-demand groups loaded for this diagnosis. After the fix is verified, release groups the next phase no longer needs with discover_tools(reset=[...]).

For timing or input regressions, invoke godot-playtest and use runtime_time_control rather than wall-clock sleeps. Stop when the original failure is reproduced red, the fix makes the same check green, and no new diagnostics appear.
