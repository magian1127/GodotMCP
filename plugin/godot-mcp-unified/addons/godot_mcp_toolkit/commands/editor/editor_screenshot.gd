@tool
extends RefCounted
## editor.screenshot:把编辑器视口捕获为 PNG 信封 — 既可以是完整的主视口,
## 也可以聚焦到单个节点(选中 + 编辑 + 恢复先前的
## 选择)。把内联图像限制到请求的 image_detail 级别,处理
## 无头(headless)情形(无视口),并对塌缩的视口自诊断:最小化的
## 窗口返回 EDITOR_VIEWPORT_UNAVAILABLE,而错误的主屏幕(非 2D/3D)
## 会通过切换到 2D/3D 屏幕并重新捕获来自愈。至于
## image_response_mode / image_detail / save_path 的处理 — 内联、磁盘还是两者,
## 内联分辨率上限,以及 res:// 或 user://screenshots/ 保存路径守卫 —
## 则由共享的 ScreenshotResponse 构建器整形。
##
## 无状态 — 处理器接收 (parameters) 并返回响应 Dictionary。
## 直接访问视口 / RenderingServer / EditorInterface / DisplayServer;
## 无头检查、路径归一化/编辑根查找以及
## 响应整形经由 Modules 别名访问。编辑器命令组抽取出的子模块,
## 经由 `preload` 别名访问。

const Modules := preload("res://addons/godot_mcp_toolkit/core/modules.gd")
const Helpers = Modules.CommandHelpers

## 低于该像素尺寸时,视口帧被视为塌缩而非可用:Godot 把每个视口钳制在
## 硬性的 2x2 下限,因此零尺寸的编辑器中央区域
## 的下限远低于该阈值。
const MIN_USABLE_DIMENSION := 16

## 切换主屏幕后的重新捕获尝试:刚完成布局的 2D/3D
## 画布需要一两个合成帧,其视口才会报告真实
## 尺寸。最坏情况是按已连接编辑器提升后的帧
## 率等上几帧 — 相比返回一张空白截图,这代价很低。
const MAX_HEAL_FRAMES := 4

## 等待一个新合成帧的墙上时钟(wall-clock)上限。frame_post_draw 在
## 渲染被暂停的编辑器上(未最小化但停止合成 — 例如会话被锁定/显示器
## 休眠/DWM 暂停)永远不会触发,裸 await 会把命令挂到服务器超时;
## 有界等待把它变成快速、可操作的 EDITOR_VIEWPORT_UNAVAILABLE。
## 10 秒覆盖失焦节流(约 10 fps)下数帧的余量。
const FRAME_WAIT_TIMEOUT_MS := 10_000

## 编辑器窗口最小化时的恢复提示:没有任何视口被合成,
## 因此无法捕获任何帧。这绝不会是无头情形 — 无头会在任何捕获之前
## 以 HEADLESS_UNSUPPORTED 短路返回,因此调用者不应追问那个
## 非原因。
const _HINT_MINIMIZED := "The editor window is minimized, so no viewport is composited — this is not headless. Retry with force_foreground_editor:true to raise the editor automatically, or restore the window yourself; for non-visual checks use script_check."

## 切换主屏幕已布局出 2D/3D 画布、却仍未合成出可用内容时的恢复提示 —
## 属于超出有界自愈能力的遮挡或时序边缘情况。
const _HINT_POST_HEAL := "Switched to a 2D/3D main screen but no viewport composited. Retry with force_foreground_editor:true, or use script_check for non-visual verification."

## 渲染在等待上限内没有合成任何帧(未最小化但暂停 — 典型如会话
## 被锁定、显示器休眠或合成器停滞)时的恢复提示。
## 提示先给代价最低且实测有效的动作:编辑器窗口失焦/未置前时合成会被节流,
## force_foreground_editor:true 会把它提前并聚焦,通常即可直接出图
## (实测:同一会话先报本错,加该参数后成功)。
const _HINT_PAUSED := "Rendering is paused (editor unfocused, session locked, display asleep, or compositor stalled) — no frame was composited in time. Retry with force_foreground_editor:true to raise and focus the editor window (this alone usually suffices), or restore the window/display yourself; for non-visual checks use script_check."


# -- 分类 ----------------------------------------------------------------------


## 依据来源(缩放前)视口尺寸对一次捕获进行分类。
##
## 帧可用时返回 [code]{"ok": true}[/code],否则返回
## [code]{"ok": false, "reason": "no_viewport_screen", "width": w, "height": h}[/code]。
## 最小化由调用处依据 [method DisplayServer.window_get_mode] 判定
## (在任何捕获/等待之前短路),因此这个纯分类器只
## 区分可用帧与塌缩的 2D/3D 画布。不依赖编辑器且
## 具确定性 — 这正是捕获决策放在这里而非内联的原因。
## [param source_width] 与 [param source_height] 是视口缩放前的
## 像素尺寸。
static func classify_capture(source_width: int, source_height: int) -> Dictionary:
	if source_width < MIN_USABLE_DIMENSION or source_height < MIN_USABLE_DIMENSION:
		return {
			"ok": false,
			"reason": "no_viewport_screen",
			"width": source_width,
			"height": source_height,
		}
	return {"ok": true}


# -- 命令 ---------------------------------------------------------------------


static func cmd_screenshot(parameters: Dictionary) -> Dictionary:
	if Modules.VersionUtils.is_headless():
		# 重定向必须对无头情形准确:script_check 是可靠的替代方案;
		# log_read(channel:'editor') 只提供运行时输出(其编辑器解析错误捕获本身
		# 在无头下也是降级的),因此它不能替代视觉验证。
		return MCPToolkitError.fail("HEADLESS_UNSUPPORTED",
			"editor.screenshot requires a display server (no viewport in headless mode)",
			"Use script_check to verify a script's parse status; log_read(channel:'editor') captures runtime output only (headless editors don't revalidate scripts, so editor parse errors aren't captured there).")

	# 在任何捕获之前就拒绝错误的 image_detail,以免拼错的值以全分辨率
	# 蒙混过关(纯尺寸计算器会把未知值当作原生尺寸 — 守卫
	# 只在这里放一次,两条捕获路径便能统一拒绝)。
	if Modules.ScreenshotResponse.detail_of(parameters).is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS",
			"image_detail must be one of [full, mid, low] (got %s)"
			% str(parameters.get("image_detail", "")))

	# save_path 校验同样前移到最小化检查与任何渲染等待之前:纯路径拒绝
	# (PATH_DENIED / 非 .png)不依赖视口,渲染暂停的编辑器上也必须
	# 立刻返回 — 否则调用方要白等完帧等待上限才拿到一个与捕获
	# 无关的确定性错误。判定与 build() 共用同一条路径。
	var early_save := Modules.ScreenshotResponse.validate_save_path_param(
		parameters, ["res://", "user://screenshots/"])
	if early_save.get("success") == false:
		return early_save

	var force_foreground := bool(parameters.get("force_foreground_editor", false))
	var remediation: Array[String] = []

	# 最小化会暂停合成,因此无法捕获任何帧。要提前
	# 检测它 — 在任何 await frame_post_draw 之前;该信号在渲染被暂停的
	# 编辑器上永远不会触发,会把处理器挂住直到服务器超时。
	if _window_is_minimized():
		if not force_foreground:
			return MCPToolkitError.fail("EDITOR_VIEWPORT_UNAVAILABLE",
				"editor viewport unavailable: the editor window is minimized", _HINT_MINIMIZED)
		await _foreground_editor()
		remediation.append("foregrounded_editor")

	var node_path := str(parameters.get("node_path", ""))
	node_path = Helpers.normalize_editor_path(node_path)

	if not node_path.is_empty():
		return await _capture_node(parameters, node_path, remediation)
	return await _capture_standard(parameters, remediation)


# -- 捕获路径 -------------------------------------------------------------------


## 等待至少一个新合成帧,带墙上时钟上限。以 Engine.get_frames_drawn()
## 作为"绘制确实发生"的观测(不依赖信号),以 SceneTree.process_frame
## 作为主循环心跳(每处理帧都触发,与绘制无关 — 注意 frame_post_draw
## 本身不可用作等待对象:渲染暂停时它永不触发,正是这里要防的挂死)。
## 超时返回 false — 意味着渲染被暂停(未最小化但停止合成),调用方应
## 返回 EDITOR_VIEWPORT_UNAVAILABLE 而不是挂到服务器超时。
static func _await_fresh_frame() -> bool:
	var tree := Engine.get_main_loop() as SceneTree
	if tree == null:
		return false  # 无主循环(理论上不可达 — 无头已被更早的守卫短路)。
	var start_frame := Engine.get_frames_drawn()
	var deadline := Time.get_ticks_msec() + FRAME_WAIT_TIMEOUT_MS
	while Engine.get_frames_drawn() <= start_frame:
		if Time.get_ticks_msec() >= deadline:
			return false
		await tree.process_frame
	return true


# 聚焦节点的截图:选中 + 编辑 + 捕获特定节点,然后恢复
# 先前的选择。错误的主屏幕会依据节点自身的
# 视口类型自愈(Node3D 用 3D,否则 2D)。
static func _capture_node(parameters: Dictionary, node_path: String, remediation: Array[String]) -> Dictionary:
	var root := Helpers.get_edited_root()
	if root == null:
		return MCPToolkitError.fail("NO_SCENE", "no edited scene")
	var node: Variant = null
	if node_path == ".":
		node = root
	else:
		node = root.get_node_or_null(node_path)
	if node == null:
		return MCPToolkitError.fail("NOT_FOUND", "no node at %s" % node_path, MCPToolkitError.HINT_NODE_PATH)

	var wants_3d := node is Node3D

	var selection := EditorInterface.get_selection()
	var prior_selection: Array = []
	if selection != null:
		for selected_node in selection.get_selected_nodes():
			prior_selection.append(selected_node)
		selection.clear()
		selection.add_node(node)
	EditorInterface.edit_node(node)
	if not await _await_fresh_frame():
		_restore_selection(selection, prior_selection)
		return MCPToolkitError.fail("EDITOR_VIEWPORT_UNAVAILABLE",
			"editor viewport unavailable: rendering paused — no frame composited within %d ms" % FRAME_WAIT_TIMEOUT_MS,
			_HINT_PAUSED)

	var image := await _grab_usable_image(wants_3d, remediation)
	if image == null:
		_restore_selection(selection, prior_selection)
		return MCPToolkitError.fail("EDITOR_VIEWPORT_UNAVAILABLE",
			"editor viewport unavailable: no 2D/3D viewport composited a usable frame", _HINT_POST_HEAL)

	_restore_selection(selection, prior_selection)

	var png_bytes := image.save_png_to_buffer()
	if png_bytes.is_empty():
		return MCPToolkitError.fail("EMPTY_CONTENT",
			"node '%s' produced no visible image. Node may lack visual content (no texture, no mesh). Use capture_screenshot(target:'editor') without node_path for a full viewport capture instead." % node_path)
	var inline := _downscale_inline_png(image, parameters, png_bytes)
	var response := Modules.ScreenshotResponse.build(
		parameters, png_bytes, image.get_width(), image.get_height(),
		["res://", "user://screenshots/"],
		inline["png_bytes"], int(inline["width"]), int(inline["height"]),
		str(inline["detail"]))
	if response.get("success") == false:
		return response
	# 内联模式回显所请求的节点路径;磁盘/两者模式的响应已在 "path" 中携带
	# FILE 路径(它取代回显 — 调用者本来就知道自己的输入)。
	if not response.has("path"):
		response["path"] = node_path
	if not remediation.is_empty():
		response["remediation"] = remediation
	return MCPToolkitSuccess.ok(response)


# 标准视口截图。自愈目标默认为 2D 视口。
static func _capture_standard(parameters: Dictionary, remediation: Array[String]) -> Dictionary:
	# 首次读取前先等待以保新鲜:否则被节流且失焦的编辑器
	# 可能交回略微过期的帧。现在安全了,因为最小化已被
	# 提前短路;若渲染在等待上限内没有合成任何帧(未最小化
	# 但暂停 — 会话锁定/显示器休眠等),快速失败而不是挂死。
	if not await _await_fresh_frame():
		return MCPToolkitError.fail("EDITOR_VIEWPORT_UNAVAILABLE",
			"editor viewport unavailable: rendering paused — no frame composited within %d ms" % FRAME_WAIT_TIMEOUT_MS,
			_HINT_PAUSED)

	var image := await _grab_usable_image(false, remediation)
	if image == null:
		return MCPToolkitError.fail("EDITOR_VIEWPORT_UNAVAILABLE",
			"editor viewport unavailable: no 2D/3D viewport composited a usable frame", _HINT_POST_HEAL)

	var png_bytes := image.save_png_to_buffer()
	if png_bytes.is_empty():
		return MCPToolkitError.fail("INTERNAL", "save_png_to_buffer returned empty")

	var inline := _downscale_inline_png(image, parameters, png_bytes)
	var response := Modules.ScreenshotResponse.build(
		parameters, png_bytes, image.get_width(), image.get_height(),
		["res://", "user://screenshots/"],
		inline["png_bytes"], int(inline["width"]), int(inline["height"]),
		str(inline["detail"]))
	if response.get("success") == false:
		return response
	if not remediation.is_empty():
		response["remediation"] = remediation
	return MCPToolkitSuccess.ok(response)


# 为请求的 image_detail 级别编码内联 PNG,并按传输缓冲自适应:从请求级别
# 逐级下降(full→mid→low),返回第一个放得进 ws_buffer_kb 的级别;全部超限
# 时返回空载荷,ScreenshotResponse.build 兜底为磁盘形态 — 绝不让超限帧
# 上升为 RESPONSE_TOO_LARGE。仅在内联确实会被返回时(inline/both 模式)执行 —
# 磁盘模式返回全分辨率文件,不需要内联缓冲。原生尺寸(该级别无需缩放)复用
# 调用方已编码的全分辨率缓冲,不重复编码。返回 {png_bytes, width, height,
# detail}:detail 是实际应用的级别,供 build 披露降级;png_bytes 为空
# (尺寸 -1)表示无内联候选 — build 解读为"内联使用全分辨率缓冲、未降级"。
static func _downscale_inline_png(image: Image, parameters: Dictionary,
		png_bytes: PackedByteArray) -> Dictionary:
	var empty := {"png_bytes": PackedByteArray(), "width": -1, "height": -1, "detail": ""}
	if Modules.ScreenshotResponse.mode_of(parameters) == "disk":
		return empty
	var requested := Modules.ScreenshotResponse.detail_of(parameters)
	var native := Vector2i(image.get_width(), image.get_height())
	var last_dims := Vector2i(-1, -1)
	for level in Modules.ScreenshotResponse.progressive_details(requested):
		var target := Modules.ScreenshotResponse.image_detail_dims(native.x, native.y, level)
		if target == last_dims:
			continue  # 图像本就小于该级别上限 — 与上一候选同尺寸,无需重复编码
		last_dims = target
		var level_bytes := png_bytes
		if target != native:
			# duplicate() 的类型是 Ref<Resource>;显式的 Image 局部变量完成强转(类型不匹配时
			# 会大声报错,不像 `as`)。对副本执行缩放,使全分辨率缓冲保持完好以供磁盘保存。
			var inline_image: Image = image.duplicate()
			inline_image.resize(target.x, target.y, Image.INTERPOLATE_LANCZOS)
			level_bytes = inline_image.save_png_to_buffer()
		if Modules.ScreenshotResponse.inline_fits_transport(level_bytes):
			return {
				"png_bytes": level_bytes,
				"width": target.x,
				"height": target.y,
				"detail": level,
			}
	return empty


# -- 视口自愈 + 前台化 -----------------------------------------------------------


# 获取可用的视口图像,并自动修复(自愈)错误的主屏幕。读取
# 所请求的视口;若其已塌缩(2x2 下限,而非最小化 — 那种情况
# 已提前处理),则切换到 2D/3D 主屏幕,并在一个有界帧循环内反复检查,
# 一旦出现可用帧立即跳出。执行过切换时,把
# "switched_main_screen" 追加到 [param remediation]。
# 返回可用的 Image;若在上限内没有任何内容合成,则返回 null。
static func _grab_usable_image(wants_3d: bool, remediation: Array[String]) -> Image:
	var image := _read_viewport_image(wants_3d)
	if image != null and classify_capture(image.get_width(), image.get_height())["ok"]:
		return image

	# 已塌缩且非最小化 ⇒ 2D/3D 画布不是活动主屏幕。
	# 对其布局并重新捕获;先前屏幕没有 getter,因此
	# 切换是单向的,通过 remediation 披露而不是恢复。
	EditorInterface.set_main_screen_editor("3D" if wants_3d else "2D")
	var switched := false
	for _attempt in range(MAX_HEAL_FRAMES):
		if not await _await_fresh_frame():
			break  # 渲染暂停 — 再多重试也不会合成帧;走 null 路径的错误契约。
		image = _read_viewport_image(wants_3d)
		if image != null and classify_capture(image.get_width(), image.get_height())["ok"]:
			switched = true
			break

	if switched:
		remediation.append("switched_main_screen")
		return image
	return null


# 读取所请求编辑器视口的当前纹理(不存在 3D 视口时,3D 回退到 2D
# 视口)。没有可用视口或纹理时
# 返回 null。
static func _read_viewport_image(wants_3d: bool) -> Image:
	var viewport: SubViewport = null
	if wants_3d:
		viewport = EditorInterface.get_editor_viewport_3d(0)
	if viewport == null:
		viewport = EditorInterface.get_editor_viewport_2d()
	if viewport == null:
		return null
	return viewport.get_texture().get_image()


# 取消最小化 + 置顶 + 聚焦编辑器窗口,然后等待一个合成帧,
# 使后续捕获看到已布局的视口。window_set_mode(WINDOWED) 仅在
# 最小化时应用,以免最大化的编辑器被取消最大化;4.6+ 已弃用的
# Window.move_to_foreground() 被有意避开,改用
# DisplayServer.window_move_to_foreground() + Window.grab_focus()。
static func _foreground_editor() -> void:
	if _window_is_minimized():
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED, 0)
	DisplayServer.window_move_to_foreground(0)
	var base := EditorInterface.get_base_control()
	if base != null:
		var win := base.get_window()
		if win != null:
			win.grab_focus()
	await _await_fresh_frame()


# -- 辅助函数 ------------------------------------------------------------------


static func _window_is_minimized() -> bool:
	return DisplayServer.window_get_mode(0) == DisplayServer.WINDOW_MODE_MINIMIZED


static func _restore_selection(selection: EditorSelection, prior_selection: Array) -> void:
	if selection == null:
		return
	selection.clear()
	for selected_node in prior_selection:
		if is_instance_valid(selected_node):
			selection.add_node(selected_node)
