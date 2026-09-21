[英文原文](input-events.md)

# `input_simulate` 事件参考

`input_simulate` 将输入注入**正在运行的游戏**（需要 `game_start`）。

## 事件结构

每个事件的结构为 `{event_type, event_data?, delay_before_ms?, delay_after_ms?}`。传入单个事件对象，或传入 `events` 数组表示一系列事件——**优先一次调用传入多个事件**，而不是分开调用。`summary: true`（默认值）会返回每个事件的精简输出。

## 事件类型

| `event_type` | `event_data` 的关键字段 | 说明 |
|--------------|-------------------------|-------|
| `key` | `keycode`, `pressed` | Godot 键常量（例如 `KEY_SPACE`） |
| `mouse_button` | `button_index`, `position`, `pressed` | `button_index`：1 = 左键，2 = 右键 |
| `mouse_motion` | `position`, `relative` | 用于拖动 / 悬停 |
| `action` | `action`, `pressed` | 与 Input Map 动作名称匹配 |
| `click` | `position`（或 `world_position`） | 组合事件：通过 `push_input` 在该位置按下 + 50 ms + 松开（对 GUI 安全；不会移动 OS 鼠标或夺取窗口焦点） |
| `click_node` | `node_path` | 调用 `grab_focus` 并在 `BaseButton` 上发出 `pressed`——无需猜测坐标 |
| `send_text` | `text`（必需）、`node_path?`、`submit?` | 通过合成逐字符键盘事件输入字符串，触发真实的 `text_changed` / `text_submitted` 信号，而直接设置 `.text` 不会触发这些信号 |

## 坐标模式（鼠标事件）

- `position: {x, y}` — 原始视口 / 屏幕坐标（默认）。用于 UI 元素（按钮、菜单）。
- `world_position: {x, y}` — 游戏世界坐标，通过画布变换自动转换（会考虑摄像机偏移和缩放）。用于点击游戏内的特定位置。

## `send_text` 返回字段

`send_text` 返回 `focus_target`、`focus_source`、`text_changed`、`text_after`（机密 / 密码字段会脱敏）、`chars_sent` 以及 `hint`（当没有控件获得焦点时，引导你传入 `node_path`）。

## 时序警告

`delay_after_ms` 会在每个事件后等待。保持在 **100–300 ms**。数值 **> 500 ms** 很危险：实时游戏世界会在等待期间继续推进——敌人移动、计时器跳动、伤害累积——因此长延迟可能改变你原本要断言的状态。

## 焦点与路由

鼠标事件通过设置了 `position` + `global_position` 的 `push_input` 路由——足以完成视口 / `CanvasLayer` / GUI 命中测试，**无需 OS 级窗口焦点或鼠标移动**；因此，同时驱动多个游戏实例的并行会话不会争抢 OS 鼠标。窗口置前是单独的显式选择（`force_foreground_game`）——输入合成从不需要它。
每个事件都会返回逐事件诊断信息，帮助调试输入最终落点。
