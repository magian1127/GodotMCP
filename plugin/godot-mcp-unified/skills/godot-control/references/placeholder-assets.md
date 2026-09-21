语言：中文 | [英文版](placeholder-assets.en.md)

# 占位素材

当任务需要简单、确定性的本地占位 PNG 或 WAV 时，使用本技能随附脚本；用户提供的真实素材继续使用 `asset_import`。

先解析包含 `project.godot` 的绝对项目根目录，并从当前技能目录解析脚本的绝对路径；不要假定工作区当前目录就是技能目录。脚本只接受项目根目录内的 `res://` 或相对输出路径，拒绝经过符号链接 / junction 逃逸，目标已存在时默认失败；只有最终状态明确要求覆盖时才传 `--replace`。

```powershell
node "<godot-control 技能目录>\scripts\generate-placeholder-texture.mjs" --project-root "D:\Games\Example" --output "res://assets/player.png" --shape circle --width 64 --height 64 --fill-color "#478cbf" --outline-color black --label P

node "<godot-control 技能目录>\scripts\generate-placeholder-sound.mjs" --project-root "D:\Games\Example" --output "res://assets/jump.wav" --waveform square --frequency 440 --end-frequency 880 --duration 0.25 --volume 0.6
```

纹理支持 `solid|circle|triangle|diamond|arrow|checkerboard|grid`、颜色、轮廓、方向和 ASCII 字母/数字标签。声音支持 `sine|square|triangle|sawtooth|noise`、音高扫频、淡入淡出和衰减，时长上限 5 秒。

脚本成功后，加载 `editor_advanced` 并对返回的 `resource_path` 调用 `editor_sync`。使用 `resource_load` 或目标节点属性确认 Godot 已导入资源；阶段结束后释放 `editor_advanced`。
