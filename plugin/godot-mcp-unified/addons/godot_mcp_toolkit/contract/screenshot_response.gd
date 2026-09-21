@tool
extends RefCounted
## 共享的截图响应构建器 — 模式解析、[code]image_detail[/code] 尺寸计算、
## 保存路径护栏、磁盘持久化,以及为三个截图处理器
## 组装 inline/disk/both 载荷;内联载荷按传输缓冲自适应 —
## 放不下时逐级降级并自动落盘(见 [method build]),绝不上升为
## RESPONSE_TOO_LARGE 错误。
##
## [code]image_response_mode[/code] 参数选择捕获结果如何返回:
## [code]"inline"[/code](默认)将 base64 PNG 嵌入响应;
## [code]"disk"[/code] 持久化 PNG 并仅返回其文件路径(用于
## 超大捕获或节省上下文令牌);[code]"both"[/code] 两者都做。
## [code]save_path[/code] 指定目标位置(按调用方的允许列表验证,
## 要求 [code].png[/code] 后缀);在 disk/both 模式下省略时,
## 自动命名的文件落在 [code]user://screenshots/[/code] 下。编辑器与
## 运行时处理器共享这一个构建器,因此线上形态在所有
## 三个调用点保持一致。[br]
## [br]
## [code]image_detail[/code] 参数([code]"full"[/code]/[code]"mid"[/code]/
## [code]"low"[/code])限制 [b]inline[/b] 图像的长边;本文件拥有
## 纯函数 [method image_detail_dims] 计算器(捕获处理器负责执行
## [Image] 缩放 — 这里保持纯塑形器)并回显应用的级别与
## 返回的 [code]"WxH"[/code],使尺寸缩减绝不会静默发生。disk/both
## 响应还会带一条提示,说明保存的文件为全分辨率。[br]
## [br]
## 设计上即为运行时干净 — 它只 preload [code]security/file_guard.gd[/code],
## 并引用值类型及 [MCPToolkitError](运行时安全的全局类)、
## [FileAccess]、[DirAccess]、[ProjectSettings]、[Marshalls]、[Image](核心类)
## 与 [Time],从不引用编辑器类。此静态图中任何位置出现编辑器符号
## 都会使运行时自动加载在未剥离的导出中解析失败
## (godotengine/godot#91713),且运行时服务器会 preload 此文件;
## 请保持它无编辑器引用。

const FileGuard := preload("res://addons/godot_mcp_toolkit/security/file_guard.gd")

## 当 disk/both 模式省略 save_path 时,自动命名捕获的写入目录。
const _AUTO_NAME_DIR := "user://screenshots/"

## 内联图像按 image_detail 级别划分的长边上限(像素)。暂定
## 目标:"mid" 保持平视显示(HUD)文本可读,"low" 仅保留大致布局/动态;
## 此处没有 "full"(永不限制)。调校后的数值有待实际保真度检查。
const IMAGE_DETAIL_CAPS := {"mid": 1024, "low": 512}

## 内联容量投影中除 base64 PNG 之外预留的字节开销:JSON-RPC 信封、载荷的
## 其余键(width/height/bytes/mime_type/image_detail/returned)与 JSON 结构
## 本身。远大于实测值,只为让投影绝不低估。
const _INLINE_ENVELOPE_OVERHEAD_BYTES := 768


## 传输缓冲上限(KB)。与 ws_transport 的 accept_pending() 同源 — 编辑器与
## 运行时两条传输都经它从该 ProjectSetting 设置每个对端的收发缓冲,因此
## 这里的读取对编辑器与 playtest 两种捕获路径都精确。
static func transport_limit_kb() -> int:
	return ProjectSettings.get_setting("mcp_toolkit/limits/ws_buffer_kb", 1024)


## 内联载荷(base64 PNG + 信封与元数据开销 + 发送护栏余量)投影后能否放进
## 传输缓冲。纯算术 — 不实际分配 base64 字符串。判稳口径与发送护栏
## ([method MCPToolkitError.guard_response_size])一致:投影 ≤ 上限 − 余量。
static func inline_fits_transport(png_bytes: PackedByteArray) -> bool:
	var base64_bytes := (png_bytes.size() + 2) / 3 * 4
	var projected := base64_bytes + _INLINE_ENVELOPE_OVERHEAD_BYTES \
		+ MCPToolkitError.SIZE_GUARD_MARGIN
	return projected <= transport_limit_kb() * 1024


## 请求的内联级别放不进传输缓冲时依次尝试的级别序列(含请求级别本身)。
## 供捕获处理器逐级降采样 — 取第一个放得下的级别;全部超限时由调用方交回
## 空载荷,build() 兜底为磁盘形态(绝不上升为 RESPONSE_TOO_LARGE 错误)。
static func progressive_details(requested: String) -> Array[String]:
	if requested == "mid":
		return ["mid", "low"]
	if requested == "low":
		return ["low"]
	return ["full", "mid", "low"]


## 返回请求的响应模式:[code]"inline"[/code]、[code]"disk"[/code]
## 或 [code]"both"[/code]。
##
## 缺失 [code]image_response_mode[/code] 时默认为 [code]"inline"[/code]。
## 存在但无法识别的值返回 [code]""[/code],使调用方可以
## 将其作为 INVALID_PARAMS 拒绝。[param parameters] 是原始 JSON-RPC 参数字典。
static func mode_of(parameters: Dictionary) -> String:
	if not parameters.has("image_response_mode"):
		return "inline"
	var mode := str(parameters.get("image_response_mode", ""))
	if mode == "inline" or mode == "disk" or mode == "both":
		return mode
	return ""


## 返回请求的内联分辨率级别:[code]"full"[/code]、
## [code]"mid"[/code] 或 [code]"low"[/code]。
##
## 缺失 [code]image_detail[/code] 时默认为 [code]"full"[/code](原生,无上限)。
## 存在但无法识别的值返回 [code]""[/code],使每个捕获处理器
## 可以将其作为 INVALID_PARAMS 拒绝,而不是静默回退到全分辨率。
## [param parameters] 是原始 JSON-RPC 参数字典。
static func detail_of(parameters: Dictionary) -> String:
	if not parameters.has("image_detail"):
		return "full"
	var detail := str(parameters.get("image_detail", ""))
	if detail == "full" or detail == "mid" or detail == "low":
		return detail
	return ""


## 应用于原生 [param width]×[param height] 帧的、由 [param detail]
## 决定的目标内联尺寸。
##
## 等比、保持宽高比、且只缩小:当 [code]"full"[/code]、未知值,
## 或长边已在该级别上限之内时,原样返回原生尺寸(从不放大)。
## 缩减级别将长边限制为 [constant IMAGE_DETAIL_CAPS],
## 并按比例缩放短边以匹配。纯函数且确定性 — 尺寸决策在此实现,
## 因此无需编辑器即可单元测试,
## 而 [method Image.resize] 调用归捕获处理器所有。
static func image_detail_dims(width: int, height: int, detail: String) -> Vector2i:
	if not IMAGE_DETAIL_CAPS.has(detail):
		return Vector2i(width, height)
	var cap: int = IMAGE_DETAIL_CAPS[detail]
	var long_edge := maxi(width, height)
	if long_edge <= cap:
		return Vector2i(width, height)
	var scale := float(cap) / float(long_edge)
	return Vector2i(maxi(1, roundi(width * scale)), maxi(1, roundi(height * scale)))


## 按请求的模式组装截图响应,在 disk/both 模式下持久化 PNG,
## 并返回处理器包装进
## [method MCPToolkitSuccess.ok] 的数据字典。
##
## [param parameters] 是原始参数字典(读取 [code]image_response_mode[/code]、
## [code]image_detail[/code] 与 [code]save_path[/code]);[param png_bytes] 是
## [b]全分辨率[/b] 编码的 PNG,[param width] / [param height] 是其原生
## 尺寸 — 磁盘副本始终持久化这些;[param allowed_save_prefixes] 是
## 此上下文的保存路径允许列表(编辑器:[code]res://[/code] +
## [code]user://screenshots/[/code];运行时:仅 [code]user://screenshots/[/code])。[br]
## [br]
## [b]inline[/b] 图像可分离,使 [code]image_detail[/code] 可以缩小它,
## 而磁盘仍保持全分辨率:传入预先缩小的 [param inline_png_bytes] +
## [param inline_width] / [param inline_height],内联 base64 便使用这些值
## 而不是全分辨率缓冲区。保持默认值(空 / [code]-1[/code])时,
## 内联复用全分辨率缓冲区 — 即 [code]"full"[/code] 路径,与
## 未应用 [code]image_detail[/code] 的捕获逐字节相同。[method Image.resize]
## 归调用方(而非这个纯塑形器)所有。每个载荷都会回显
## 应用的 [code]image_detail[/code] 及 [code]returned[/code](返回内联的
## [code]"WxH"[/code],或 disk 模式下全分辨率文件的),使尺寸缩减绝不会静默发生。[br]
## [br]
## 只要存在,[code]save_path[/code] 就会在任意模式下被验证:它必须
## 通过 [method FileGuard.resolve_safe] 对照 [param allowed_save_prefixes]
## 检查,并以 [code].png[/code] 结尾。在 inline 模式下它会被验证,
## 但不会持久化(响应与不保存的捕获逐字节相同)。在 disk/both
## 模式下,目标是给定的 [code]save_path[/code],否则是
## [code]user://screenshots/[/code] 下的自动命名文件;目录会被创建并
## 写入全分辨率 PNG,且 [code]hint[/code] 会披露保存的文件为全分辨率。[br]
## [br]
## 成功时返回组装好的载荷:
## [code]{image_base64, mime_type, width, height, bytes, image_detail, returned}[/code]
## 用于 inline;[code]{path, width, height, bytes, mime_type, image_detail, returned,
## hint}[/code](无 base64)用于 disk;inline 形状加 [code]path[/code] 与
## [code]hint[/code] 用于 both。返回的 [code]path[/code] 是全局化的绝对
## 文件路径。失败时直接返回 [method MCPToolkitError.fail] 字典
## (以 [code]"success": false[/code] 识别):INVALID_PARAMS(模式值错误或
## 非 [code].png[/code] 的 save_path)、PATH_DENIED(FileGuard 拒绝),
## 或 INTERNAL(创建目录 / 写入失败)。[br]
## [br]
## [b]传输缓冲自适应[/b](内联模式的交付底线 — 超限帧会被对端的发送护栏整体
## 换成 RESPONSE_TOO_LARGE,与其让调用方吃错误再重试,不如首次就交付能投递的
## 形态):[param applied_detail] 披露捕获处理器实际应用的内联级别(逐级
## 降采样后可能低于请求级别;空串 = 未降级)。内联载荷放不下缓冲时,本构建器
## 自动把全分辨率 PNG 持久化([param save_path] 或自动命名)并返回磁盘形态,
## [code]hint[/code] 说明原因与后续路径;降级放行(放得下但低于请求级别)时,
## 额外持久化全分辨率副本 — 内联即时可看,像素细节经 [code]path[/code] 免二次
## 捕获。仅当连落盘都失败时才返回 INTERNAL(此时确实无路可走)。
static func build(parameters: Dictionary, png_bytes: PackedByteArray, width: int, height: int,
		allowed_save_prefixes: Array, inline_png_bytes: PackedByteArray = PackedByteArray(),
		inline_width: int = -1, inline_height: int = -1, applied_detail: String = "") -> Dictionary:
	var mode := mode_of(parameters)
	if mode.is_empty():
		return MCPToolkitError.fail("INVALID_PARAMS",
			"image_response_mode must be one of [inline, disk, both] (got %s)"
			% str(parameters.get("image_response_mode", "")))

	var save_path := str(parameters.get("save_path", ""))
	# 应用的内联分辨率级别,在每个形状上回显。处理器已验证过该枚举;缺失 →
	# "full"(原生)。处理器逐级降采样时经 [param applied_detail] 披露实际级别,
	# 使尺寸缩减绝不静默。disk 模式不返回内联图像,因此其 "returned" 始终报告
	# 全分辨率尺寸,与此值无关。
	var detail := str(parameters.get("image_detail", "full"))
	var applied := applied_detail if not applied_detail.is_empty() else detail
	# 降级放行的披露前缀:实际内联级别低于请求级别时说明原因,拼在每个
	# 携带 hint 的载荷之前。
	var downgrade_note := ""
	if applied != detail:
		downgrade_note = "Inline image downgraded from '%s' to '%s' to fit the %d KB WebSocket transport buffer. " \
			% [detail, applied, transport_limit_kb()]

	# 当调用方提供了预先缩小的缓冲区时,内联图像使用它;
	# 否则使用全分辨率缓冲区("full" 路径)。disk 模式忽略这些 — 无内联。
	var has_inline_override := inline_png_bytes.size() > 0 and inline_width >= 0 and inline_height >= 0
	var inline_bytes := inline_png_bytes if has_inline_override else png_bytes
	var inline_w := inline_width if has_inline_override else width
	var inline_h := inline_height if has_inline_override else height

	# 在每种模式下都验证提供的 save_path,使 inline 调用方获得
	# 与 disk/both 相同的确定性拒绝,而不是静默接受。
	if not save_path.is_empty():
		var validation := _validate_save_path(save_path, allowed_save_prefixes)
		if validation.get("success") == false:
			return validation

	if mode == "disk":
		var target := save_path if not save_path.is_empty() else _auto_name()
		var persisted := _persist_png(target, png_bytes)
		if persisted.get("success") == false:
			return persisted
		# 仅 disk:返回的产物是全分辨率文件,因此 "returned" = 全分辨率尺寸。
		return _disk_payload(str(persisted["path"]), png_bytes, width, height, detail)

	# inline / both:内联载荷必须放得进传输缓冲 — 超限帧会被对端的发送护栏
	# 整体换成 RESPONSE_TOO_LARGE 错误。放不下时直接交付磁盘形态(自动落盘
	# 全分辨率),把今天的"错误 → 按 hint 重试 disk"两轮往返压缩为零。
	if not inline_fits_transport(inline_bytes):
		var fallback_target := save_path if not save_path.is_empty() else _auto_name()
		var persisted_fb := _persist_png(fallback_target, png_bytes)
		if persisted_fb.get("success") == false:
			return persisted_fb
		var disk := _disk_payload(str(persisted_fb["path"]), png_bytes, width, height, detail)
		disk["hint"] = _inline_overflow_hint(str(persisted_fb["path"]), inline_bytes.size())
		return disk

	if mode == "inline":
		# 降级放行:内联即时可看,同时持久化全分辨率副本使像素细节无需二次
		# 捕获。落盘失败不拦截交付 — 内联本身放得下,降级已由 image_detail
		# 回显披露。
		var payload := _inline_payload(inline_bytes, inline_w, inline_h, applied)
		if applied != detail:
			var downgrade_target := save_path if not save_path.is_empty() else _auto_name()
			var persisted_dg := _persist_png(downgrade_target, png_bytes)
			# _persist_png 成功时只带 "path"(无 success 键);按本文件其余分支的
			# 口径,仅显式 false 视为失败。
			if persisted_dg.get("success", true) != false:
				payload["path"] = str(persisted_dg["path"])
				payload["hint"] = downgrade_note + _full_res_hint(str(persisted_dg["path"]))
			else:
				payload["hint"] = downgrade_note.strip_edges()
		return payload

	# both:确定目标位置,确保目录存在,写入全分辨率 PNG。
	var target_both := save_path if not save_path.is_empty() else _auto_name()
	var persisted_both := _persist_png(target_both, png_bytes)
	if persisted_both.get("success") == false:
		return persisted_both
	var globalized := str(persisted_both["path"])
	var payload_both := _inline_payload(inline_bytes, inline_w, inline_h, applied)
	payload_both["path"] = globalized
	payload_both["hint"] = downgrade_note + _full_res_hint(globalized)
	return payload_both


## 通过 FileGuard 加 [code].png[/code] 后缀检查,对照
## [param allowed_prefixes] 验证 [param save_path]。有效时返回 [code]{}[/code],
## 否则返回 PATH_DENIED / INVALID_PARAMS 的 [method MCPToolkitError.fail] 字典。
static func _validate_save_path(save_path: String, allowed_prefixes: Array) -> Dictionary:
	var guard := FileGuard.resolve_safe(save_path, allowed_prefixes)
	if guard["error"] != null:
		return MCPToolkitError.fail("PATH_DENIED", str(guard["reason"]))
	if not save_path.ends_with(".png"):
		return MCPToolkitError.fail("INVALID_PARAMS",
			"save_path must end with .png: %s" % save_path)
	return {}


## 预校验 parameters 里调用方提供的 save_path(缺失或有效返回 [code]{}[/code],
## 无效返回错误字典)。供捕获处理器在任何渲染等待/最小化检查之前先行拒绝 —
## 纯路径错误(PATH_DENIED / 非 .png)不应依赖视口可用:渲染被暂停的
## 编辑器上它也必须立刻、确定性地返回,而不是等完帧等待上限。
## 与 [method build] 内部的校验共用同一条 [_validate_save_path] 判定,
## 因此两个时点绝不给出相异的裁决。
static func validate_save_path_param(parameters: Dictionary, allowed_save_prefixes: Array) -> Dictionary:
	var save_path := str(parameters.get("save_path", ""))
	if save_path.is_empty():
		return {}
	return _validate_save_path(save_path, allowed_save_prefixes)


## 将 [param png_bytes] 写入 [param target](已验证的 res:// 或 user:// 路径),
## 创建父目录。成功时返回 [code]{"path": <globalized absolute path>}[/code],
## 否则返回 INTERNAL 的 [method MCPToolkitError.fail] 字典。
static func _persist_png(target: String, png_bytes: PackedByteArray) -> Dictionary:
	var directory_path := target.get_base_dir()
	if not directory_path.is_empty():
		var mkdir_error := DirAccess.make_dir_recursive_absolute(directory_path)
		if mkdir_error != OK and mkdir_error != ERR_ALREADY_EXISTS:
			return MCPToolkitError.fail("INTERNAL",
				"could not create %s (err %d)" % [directory_path, mkdir_error])
	var file := FileAccess.open(target, FileAccess.WRITE)
	if file == null:
		return MCPToolkitError.fail("INTERNAL",
			"could not open %s for writing (err %d)" % [target, FileAccess.get_open_error()])
	file.store_buffer(png_bytes)
	file.close()
	return {"path": ProjectSettings.globalize_path(target)}


## 内联载荷:base64 PNG 及其元数据与应用级别
## 披露信息,历史键在前,两个披露键追加在后。
static func _inline_payload(png_bytes: PackedByteArray, width: int, height: int,
		detail: String) -> Dictionary:
	return {
		"image_base64": Marshalls.raw_to_base64(png_bytes),
		"mime_type": "image/png",
		"width": width,
		"height": height,
		"bytes": png_bytes.size(),
		"image_detail": detail,
		"returned": "%dx%d" % [width, height],
	}


## 精简的磁盘载荷:文件路径加元数据与应用级别
## 披露信息,不嵌入 base64。[param width] / [param height] 是
## 保存文件的全分辨率尺寸(disk 模式从不缩小)。
static func _disk_payload(globalized_path: String, png_bytes: PackedByteArray,
		width: int, height: int, detail: String) -> Dictionary:
	return {
		"path": globalized_path,
		"width": width,
		"height": height,
		"bytes": png_bytes.size(),
		"mime_type": "image/png",
		"image_detail": detail,
		"returned": "%dx%d" % [width, height],
		"hint": _full_res_hint(globalized_path),
	}


## 磁盘持久化披露提示:保存的文件始终是全分辨率,
## 与 [code]image_detail[/code] 无关,因此智能体可以直接读取它以获取像素细节,
## 而无需重新捕获。[param globalized_path] 是保存文件的绝对路径。
static func _full_res_hint(globalized_path: String) -> String:
	return "Saved full-res → %s. Read it for pixel detail without re-capturing." % globalized_path


## 内联超限自动落盘的披露提示:说明为什么没有内联图像(投影编码大小对
## [code]ws_buffer_kb[/code] 上限)、全分辨率文件在哪,以及想要内联图像时的
## 三条路 — 更低的 [code]image_detail[/code] 级别、提高缓冲上限。
## [param inline_byte_count] 是未能放行的内联 PNG 原始字节数(按 base64
## 最坏情况投影,与 [method inline_fits_transport] 同口径)。
static func _inline_overflow_hint(globalized_path: String, inline_byte_count: int) -> String:
	var projected_kb := (inline_byte_count + 2) / 3 * 4 / 1024
	return "Inline image (~%d KB encoded) exceeded the %d KB WebSocket transport buffer, so the capture was saved to disk instead → %s. Read the file for full pixel detail; request image_detail:'mid' or 'low' for an inline image, or raise mcp_toolkit/limits/ws_buffer_kb to allow larger inline captures." \
		% [projected_kb, transport_limit_kb(), globalized_path]


## 为无保存路径的 disk/both 捕获生成 [code]user://screenshots/[/code] 下的
## 唯一自动命名。人类可排序的时间戳加微秒滴答,
## 使同一秒内的两次捕获绝不会冲突。
static func _auto_name() -> String:
	var stamp := Time.get_datetime_string_from_system(false, false).replace(":", "-")
	return "%sscreenshot_%s_%d.png" % [_AUTO_NAME_DIR, stamp, Time.get_ticks_usec()]
