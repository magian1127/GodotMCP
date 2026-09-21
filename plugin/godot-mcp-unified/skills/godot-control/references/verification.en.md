Language: English | [中文](verification.md)

# Verification

Choose the shortest chain that proves the requested outcome.

## Authoring changes

1. Re-read the affected node, scene, resource, or setting.
2. Save the scene where applicable.
3. Run script_check on changed GDScript; use lsp_diagnostics(scope="project") when changes cross files.
4. Start the relevant scene/project if a display is available.
5. Inspect runtime state or logs. Use a screenshot only for appearance.

## Gameplay changes

Use godot-playtest: run, freeze if timing matters, inject bounded input, advance exact frames or until a condition, read state/logs, then thaw and stop only the session you started.

## Headless environments

HEADLESS_UNSUPPORTED for a display-bound screenshot or playtest is a valid environmental result, not functional proof. Still run parse/LSP checks and editor-side smoke checks, and label visual/runtime acceptance as outstanding.

## Completion evidence

Report the project path, Godot version, saved artifacts, validation commands/tools, and concrete observed values. A successful tool response alone is not evidence that the game behaves correctly.
