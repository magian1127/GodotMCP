@tool
extends RefCounted
## 针对 input_simulate `send_text` 事件的纯函数、可无头测试的辅助方法:
## 逐码点(codepoint)的 [InputEventKey] 合成、提交用回车键对、`text_after`
## 的截断/抹除,以及聚焦/诊断提示。
##
## 从构造上就保持运行时纯净 — 它只引用 [InputEventKey]、[String] 和字符串
## 格式化,绝不引用编辑器类 — 因此可以安全地放在运行时自动加载(autoload)
## 的 preload 闭包内。该静态依赖图中的任何编辑器符号都会让自动加载在未裁剪
## 的导出(unstripped export)中解析失败(godotengine/godot#91713),而运行时
## 服务器会 preload 本文件;请保持它不依赖编辑器。

## 截断生效之前,`text_after` 回显的最大长度。
const TEXT_AFTER_CAP := 200


## 为 [param text] 的每个码点(codepoint)合成一对按下+释放的 [InputEventKey],
## 将 `unicode` 设为该码点,`keycode` 保持为 0(仅 unicode)。
##
## 仅用 unicode,这样输入的字符通过焦点持有者的 `gui_input` 驱动文本输入,
## 同时不会误触基于键码(keycode)的快捷键。[param text] 按码点遍历
## ([method String.unicode_at]);Godot 的 [String] 是 UTF-32,因此每个
## 索引就是一个完整码点,非 ASCII 字符无需代理对(surrogate)处理。
## 返回按下、释放、按下、释放…… — 数量为码点数的两倍。
static func synthesize_text_events(text: String) -> Array[InputEventKey]:
	var events: Array[InputEventKey] = []
	for i in text.length():
		var codepoint := text.unicode_at(i)
		var press := InputEventKey.new()
		press.unicode = codepoint
		press.pressed = true
		events.append(press)
		var release := InputEventKey.new()
		release.unicode = codepoint
		release.pressed = false
		events.append(release)
	return events


## 为可选的 `submit` 回车合成按下+释放的 [InputEventKey] 键对,
## 以 `keycode = KEY_ENTER` 为准(而非 unicode)。
##
## 回车是唯一的键码例外:`ui_text_submit` / `ui_text_newline` 按键码
## (KEYCODE)匹配,因此仅 unicode 的事件既不会在 [LineEdit] 上触发
## `text_submitted`,也不会在多行 [TextEdit] 中插入换行。
static func synthesize_enter() -> Array[InputEventKey]:
	var press := InputEventKey.new()
	press.keycode = KEY_ENTER
	press.pressed = true
	var release := InputEventKey.new()
	release.keycode = KEY_ENTER
	release.pressed = false
	return [press, release]


## [method synthesize_text_events] 为 [param text] 将输入的码点数 — 即其
## 上报的 `chars_sent`。Godot 的 [String] 是 UTF-32,所以就是它的长度。
static func char_count(text: String) -> int:
	return text.length()


## 根据目标的真实文本 [param raw] 构建 `text_after` 结果字段。
##
## 抹除优先于截断:当 [param is_secret] 时,值被替换为仅其长度
## (对应 `secret = true` 的 [LineEdit]),因此秘密字符永远不会进入响应 —
## 但上游的 `text_changed` 仍然基于真实值计算。超过 [param cap] 的非秘密值
## 会被截断并加上 "...[+N chars]" 后缀(与 execute.code 的日志截断形式相同);
## 否则原样返回。
static func format_text_after(raw: String, is_secret: bool, cap: int = TEXT_AFTER_CAP) -> String:
	if is_secret:
		return "[redacted: %d chars]" % raw.length()
	if raw.length() > cap:
		return raw.substr(0, cap) + "...[+%d chars]" % (raw.length() - cap)
	return raw


## 为 send_text 结果构建可操作的 `hint`,顺利成功时返回 ""。
##
## [param text_changed] 是三态(true / false / null);null 表示目标没有
## 可读的 `text` 属性(自定义 `_input` 读取器),这不是错误,也不给出提示。
## "none" 的 [param focus_source] 引导调用方改用 `event_data.node_path`;
## 可编辑目标未发生变化时会标记可能的原因,并且当 [param tree_paused] 时
## 说明暂停的场景树会跳过 `gui_input`。node_path 无法解析的情况由处理器
## (handler)在内联中提示(路径在它手里)。
static func build_hint(focus_source: String, text_changed: Variant, focus_path: String,
		focus_class: String, chars_sent: int, tree_paused: bool) -> String:
	if focus_source == "none":
		return "No Control had focus; pass event_data.node_path to focus the target field before typing. (Custom _input handlers may still have received the %d characters.)" % chars_sent
	if text_changed == false:
		var changed_hint := "Focus was on %s (%s) but its text didn't change — non-editable, at max_length, or not a text field. Verify the target or pass node_path." % [focus_path, focus_class]
		if tree_paused:
			changed_hint += " The scene tree is paused (process_mode), so gui_input was skipped."
		return changed_hint
	return ""
