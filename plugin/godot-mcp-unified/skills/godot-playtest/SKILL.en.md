---
name: godot-playtest
description: Verify Godot gameplay, input, timing, animation, UI interaction, runtime state, or visual behavior with deterministic MCP-driven playtests. Use when the user asks to test, play, observe, compare before/after, reproduce runtime behavior, or prove a game change works.
---

Language: English | [中文](SKILL.md)

# Godot Playtest

Build a bounded, repeatable evidence loop.

1. Record whether a game is already running. Start it with game_start(wait_for_runtime=true) only when needed; stop only a session you started.
2. Load runtime_advanced using discover_tools(include_schemas=true), recording whether this playtest loaded it.
3. Capture a structured baseline with runtime_inspect_node, signals, or log_read(channel="runtime"). Reserve capture_screenshot(target="runtime") for appearance.
4. For timing-sensitive behavior:
   - runtime_time_control(action="freeze").
   - Send input as one input_simulate event sequence where possible. A pressed action may span a step; release it explicitly afterward.
   - Advance with runtime_time_control(action="step", frames=N, report=[...]), or stop on a bounded condition with action="step_until", until, and max_frames.
   - Inspect state at the frozen stop frame before advancing again.
5. Compare the observed state with an explicit expected value. Use screenshots for layout/appearance and structured state/logs for behavior.
6. Always thaw in cleanup. Release held inputs, stop the game if this run started it, and release runtime_advanced with discover_tools(reset=["runtime_advanced"]) when this playtest loaded it and the next phase no longer needs it.

Avoid blind wall-clock sleeps, coordinate guesses when click_node is available, and screenshot-only conclusions for stateful behavior. A passing playtest states the initial condition, input sequence, frame/condition bound, and final observed values.
