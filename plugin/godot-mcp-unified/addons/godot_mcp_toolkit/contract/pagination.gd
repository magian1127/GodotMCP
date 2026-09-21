@tool
extends RefCounted
## 自描述分页封装的共享构建器。
##
## 每个分页工具都返回一种可学习的形状,使大语言模型(LLM)调用方能够以相同方式
## 翻页任何结果集。此举将封装集中化 — 不变字段、恢复字段的
## 算术计算与提示包含门控 — 因此任何命令处理器都无需
## 手工实现,且词汇表在工具之间绝不会漂移。
##
## 两个维度描述一页:[br]
## [b]不变项[/b] — [code]returned[/code](本页行数)、[code]total_<unit>[/code]
## (超过任何上限后的完整计数)以及 [code]has_more[/code] — 每页都盖上。[br]
## [b]恢复[/b] — 对可恢复工具是线性恢复字段([code]next_offset[/code] /
## [code]next_start_line[/code]),或是在 [code]hint[/code] 中呈现的
## 有文档说明的无游标导航(缩小筛选范围 / 查询某个区域)。
##
## 编辑器命令处理器通过 [code]Modules.Pagination[/code] 访问它,
## 运行时自动加载则通过直接的 [code]preload[/code] 别名(它无法导入被编辑器
## 污染的 [code]Modules[/code] 聚合器)。此脚本不引用任何仅编辑器存在的
## 符号,因此它在导出模板解析运行时闭包时依然有效。
##
## 提示的 [i]措辞[/i] 由调用方决定:每个工具自行组织其恢复/导航
## 句子(相对其线上契约保持字节稳定)并传入;此构建器
## 只决定是否包含它。当 [param has_more] 为 true 或发生了
## 钳制(由 [param extras] 中的 [code]limit_clamped[/code] 标示)时,提示会出现。


## 将分页不变项盖上到页面字典并返回它。
##
## 向 [param page](调用方已预先用其条目与任何回显窗口参数
## 塑形的字典)添加 [code]returned[/code]、[code]total_<unit>[/code] 和 [code]has_more[/code]。
## 当 [param has_more] 为 true 且 [param resume_field] 非空时,添加
## 设为 [param resume_value] 的恢复字段 [param resume_field];无游标工具
## 传入空的 [param resume_field],便不会得到恢复字段。逐字合并 [param extras]
## (例如 [code]limit_clamped[/code]、工具回显的筛选条件),然后在
## [param has_more] 为 true 或 [param extras] 携带 [code]limit_clamped[/code]
## 且提示非空时添加 [param hint]。
##
## [param unit] 命名该计数描述的总额(例如 "matches"、"classes"、
## "bytes"、"lines")。返回 [param page] 本身,就地修改,作为裸字典 —
## 调用方将其包装进 [MCPToolkitSuccess]、与上下文合并,或嵌套它。
## [codeblock]
## var page := Pagination.build({"nodes": rows, "offset": offset, "limit": limit},
##     "matches", total, rows.size(), offset + rows.size() < total,
##     "next_offset", offset + rows.size(),
##     "more matches remain — page with next_offset until has_more is false")
## return MCPToolkitSuccess.ok(page)
## [/codeblock]
static func build(page: Dictionary, unit: String, total: int, returned: int,
		has_more: bool, resume_field: String, resume_value: int, hint: String,
		extras: Dictionary = {}) -> Dictionary:
	page["returned"] = returned
	page["total_" + unit] = total
	page["has_more"] = has_more
	if has_more and resume_field != "":
		page[resume_field] = resume_value
	for key in extras.keys():
		page[key] = extras[key]
	if (has_more or extras.has("limit_clamped")) and not hint.is_empty():
		page["hint"] = hint
	return page


## 列表(LIST)家族 — 索引分页页面(scene.query、classdb.search)。
##
## 从 [param items] 计算 [code]returned[/code],并从 [param offset] 对照
## [param total] 计算 [code]has_more[/code],然后通过 [method build] 将不变项
## (以及当可恢复且还有更多时的 [code]next_offset[/code] 恢复字段)
## 盖上到调用方提供的 [param page]。调用方预先以其条目与任何回显窗口
## 参数塑形 [param page],因此每个工具保持自己的字段集与顺序。
## [param unit] 命名总额(例如 "matches"、"classes")。[param hint] 是
## 调用方组合好的恢复/钳制句子。
##
## 对无游标的列表工具请将 [param resumable] 传为 false:[code]next_offset[/code]
## 会被抑制,改由调用方的 [param hint] 携带导航指导。
## 返回裸页面字典。
static func list_page(page: Dictionary, items: Array, offset: int, unit: String,
		total: int, hint: String, extras: Dictionary = {},
		resumable: bool = true) -> Dictionary:
	var returned := items.size()
	var has_more := offset + returned < total
	var resume_field := "next_offset" if resumable else ""
	return build(page, unit, total, returned, has_more, resume_field, offset + returned,
		hint, extras)


## 内容(CONTENT)字节家族 — 字节窗口页面(save.read)。
##
## 从 [param offset] 与 [param returned_bytes] 对照 [param total_bytes] 计算
## [code]has_more[/code],然后通过 [method build] 将不变项(单位为 "bytes")
## 与 [code]next_offset[/code] 字节恢复字段盖上到调用方提供的 [param page]。
## [param returned_bytes] 是该窗口中的字节数(解码后可能
## 与内容字符串长度不同)。调用方预先以其内容与回显的 [code]offset[/code]
## 塑形 [param page]。[param hint] 是调用方组合好的
## 恢复句子。
##
## 与 [method list_page] 不同,[code]next_offset[/code] 在每个窗口都会发出
## (不仅在有更多内容时):字节偏移始终是有效的恢复点,
## 因此分页调用方可通过 [code]next_offset == total_bytes[/code] 检测完成。返回裸页面。
static func byte_page(page: Dictionary, offset: int, returned_bytes: int,
		total_bytes: int, hint: String, extras: Dictionary = {}) -> Dictionary:
	var next_offset := offset + returned_bytes
	var has_more := next_offset < total_bytes
	page["next_offset"] = next_offset
	return build(page, "bytes", total_bytes, returned_bytes, has_more,
		"", 0, hint, extras)


## 内容(CONTENT)行家族 — 行窗口页面(script.read、日志读取器)。
##
## 将 [code]has_more[/code] 计算为 [param end_line] < [param total_lines],
## 然后通过 [method build] 将不变项(单位为 "lines")以及
## (当还有更多时)[code]next_start_line[/code] 恢复字段(从 1 开始计数,
## 为 [param end_line] + 1)盖上到调用方提供的 [param page]。
## [param returned] 是该窗口中的行数(请显式传入 — 当来源发生钳制时,
## 它不必等于 [code]end_line - start_line + 1[/code])。调用方预先以其内容
## 与回显的行边界塑形 [param page]。[param hint] 是调用方组合好的恢复句子。返回裸页面字典。
static func line_page(page: Dictionary, end_line: int, returned: int, total_lines: int,
		hint: String, extras: Dictionary = {}) -> Dictionary:
	var has_more := end_line < total_lines
	return build(page, "lines", total_lines, returned, has_more,
		"next_start_line", end_line + 1, hint, extras)
