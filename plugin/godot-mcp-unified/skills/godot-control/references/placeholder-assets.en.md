Language: English | [中文](placeholder-assets.md)

# Placeholder assets

Use the bundled scripts when a task needs a simple, deterministic local placeholder PNG or WAV. Continue to use `asset_import` for real assets supplied by the user.

Resolve the absolute project root containing `project.godot` first, and resolve each script's absolute path from the current skill directory; do not assume the workspace working directory is the skill directory. The scripts accept only a `res://` or relative output inside that root, reject symlink/junction escapes, and fail when the target exists. Pass `--replace` only when the requested final state explicitly requires replacement.

```powershell
node "<godot-control skill directory>\scripts\generate-placeholder-texture.mjs" --project-root "D:\Games\Example" --output "res://assets/player.png" --shape circle --width 64 --height 64 --fill-color "#478cbf" --outline-color black --label P

node "<godot-control skill directory>\scripts\generate-placeholder-sound.mjs" --project-root "D:\Games\Example" --output "res://assets/jump.wav" --waveform square --frequency 440 --end-frequency 880 --duration 0.25 --volume 0.6
```

Textures support `solid|circle|triangle|diamond|arrow|checkerboard|grid`, colours, outlines, direction, and ASCII letter/digit labels. Sounds support `sine|square|triangle|sawtooth|noise`, pitch sweeps, fades, and decay, with a five-second duration cap.

After the script succeeds, load `editor_advanced` and call `editor_sync` for the returned `resource_path`. Confirm Godot imported the asset with `resource_load` or the target node property, then release `editor_advanced` when the phase ends.
