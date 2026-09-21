@tool
extends RefCounted
## 停靠面板(dock)的分区卡片工厂——带样式的卡片与可折叠分区。
##
## 一个无状态的 `static func` 辅助类（无实例）。
## 停靠面板及其子面板会构建多个外观一致的“分区”块
## （带标题、容纳分区控件的卡片，或带切换头的可折叠变体）；
## 这种卡片形态——它的样式盒(stylebox)、头部与内容布局——
## 就集中在这唯一一处。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")


## 构建所有分区卡片与底栏共享的主题自适应样式盒(stylebox)。
## 采样编辑器的“Panel”样式盒作为基础颜色，使卡片在当前编辑器主题下呈现为
## 更深、带边框的内嵌效果。每次调用都返回新实例（StyleBoxFlat 归覆盖它的
## Control 所有——参见 §B.7）。
static func make_section_style() -> StyleBoxFlat:
	var style := StyleBoxFlat.new()
	var scale := EditorInterface.get_editor_scale()
	# 采样编辑器的 Panel 样式盒，作为主题自适应的基础颜色。
	var base := Color(0.22, 0.22, 0.22)
	var theme := Modules.EditorAccess.get_editor_theme()
	if theme:
		var sb = theme.get_stylebox("panel", "Panel")
		if sb is StyleBoxFlat:
			base = sb.bg_color
	style.bg_color = base.darkened(0.12)
	style.border_color = base.lightened(0.15)
	style.set_border_width_all(1)
	style.set_corner_radius_all(int(3 * scale))
	style.content_margin_left = 8.0 * scale
	style.content_margin_right = 8.0 * scale
	style.content_margin_top = 6.0 * scale
	style.content_margin_bottom = 6.0 * scale
	return style


## 构建一个带头部与内容 VBox 的样式化分区卡片。
## 通过  section.get_meta("content") 访问内容 VBox。
static func make_section(title: String) -> PanelContainer:
	var panel := PanelContainer.new()
	panel.add_theme_stylebox_override("panel", make_section_style())
	panel.size_flags_vertical = Control.SIZE_EXPAND_FILL

	var outer := VBoxContainer.new()
	outer.size_flags_vertical = Control.SIZE_EXPAND_FILL
	panel.add_child(outer)

	var header := Label.new()
	header.text = title
	header.add_theme_font_size_override("font_size", 13)
	outer.add_child(header)
	outer.add_child(HSeparator.new())

	var content := VBoxContainer.new()
	content.size_flags_vertical = Control.SIZE_EXPAND_FILL
	outer.add_child(content)
	panel.set_meta("content", content)
	return panel


## 构建一个带样式化标题栏和独立 ScrollContainer 的可折叠分区。标题栏会被添加到
## `parent` 中，因此它永远不会随滚动移出视野；只有分区内容会滚动。返回内容
## VBoxContainer。
static func make_collapsible(parent: VBoxContainer, title: String, expanded: bool, min_height: float = 75.0) -> VBoxContainer:
	var header := PanelContainer.new()
	header.add_theme_stylebox_override("panel", make_section_style())
	parent.add_child(header)

	var toggle := Button.new()
	toggle.flat = true
	toggle.toggle_mode = true
	toggle.button_pressed = expanded
	toggle.text = "%s %s" % ["▼" if expanded else "▶", title]
	toggle.alignment = HORIZONTAL_ALIGNMENT_LEFT
	header.add_child(toggle)

	var content_scroll := ScrollContainer.new()
	content_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	content_scroll.custom_minimum_size.y = int(min_height * EditorInterface.get_editor_scale())
	content_scroll.visible = expanded
	parent.add_child(content_scroll)

	var content := VBoxContainer.new()
	content.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	content_scroll.add_child(content)

	var t := title  # 供 lambda 捕获使用
	toggle.toggled.connect(func(pressed: bool):
		content_scroll.visible = pressed
		toggle.text = "%s %s" % ["▼" if pressed else "▶", t]
	)

	return content
