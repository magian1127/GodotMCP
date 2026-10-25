---
name: godot-playtest
description: Verify Godot gameplay, input, timing, animation, UI interaction, runtime state, or visual behavior with deterministic MCP-driven playtests. Use when the user asks to test, play, observe, compare before/after, reproduce runtime behavior, or prove a game change works.
---

Language: English | [中文](SKILL.md)

# Godot Playtest

First select this session’s target with `godot_connect(project="<absolute Godot project path>")`. Before connecting, only the entry tool exists; afterward nine core tools are available. Activate other capabilities with `discover_tools(request=[group names], include_schemas=true)`. Pass `instance` when interleaving projects. See [tool routing](../godot-control/references/tool-routing.en.md) for schemas and recovery.

Build a bounded, repeatable evidence loop.

1. Activate `playtest` with `discover_tools(request="playtest", include_schemas=true)` and record whether this test loaded it.
2. Record whether a game is already running. Start it with `game_start(wait_for_runtime=true)` only when needed; stop only a game this test started.
3. Capture a structured baseline with runtime_inspect_node, signals, or log_read(channel="runtime"). Reserve capture_screenshot(target="runtime") for appearance.
4. For timing-sensitive behavior:
   - runtime_time_control(action="freeze").
   - Send input as one input_simulate event sequence where possible. A pressed action may span a step; release it explicitly afterward.
   - Advance with runtime_time_control(action="step", frames=N, report=[...]), or stop on a bounded condition with action="step_until", until, and max_frames.
   - Inspect state at the frozen stop frame before advancing again.
5. Compare the observed state with an explicit expected value. Use screenshots for layout/appearance and structured state/logs for behavior.
6. Always thaw in cleanup. Release held inputs, stop the game if this run started it, and release playtest with discover_tools(reset=["playtest"]) when this playtest loaded it and the next phase no longer needs it.

Avoid blind wall-clock sleeps, coordinate guesses when click_node is available, and screenshot-only conclusions for stateful behavior. A passing playtest states the initial condition, input sequence, frame/condition bound, and final observed values.
