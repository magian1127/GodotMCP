Language: English | [中文](testing.md)

# Writing Godot tests

When the user asks for tests around a script, first inspect whether the project actually uses GUT, GdUnit4, or another framework. Read the target script and nearby tests, then follow the repository's directory, base-class, naming, and assertion conventions.

Create or patch the test with Codex workspace file tools. Cover the requested public behavior, boundary inputs, and failure paths; do not mechanically generate shallow tests for every method. Call `editor_sync` after editing, run `script_check` on relevant GDScript, and use `lsp_diagnostics(scope="project")` for cross-file impact. When the repository provides a test command, run the narrowest relevant command and report its observed result.
