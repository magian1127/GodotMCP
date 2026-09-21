@tool
extends RefCounted
## 纯函数、可无头测试的属性写入静默丢失检测器。
##
## Godot 的 [method Object.set] 是 void,并会丢弃无法赋值给
## 目标属性的 Variant(类型错误),因此一次写入可能"成功"而值
## 从未落地 — 这是虚假成功,会让大语言模型(LLM)调用方去追查一个从未存储的值。
## 此模块用前后回读包围一次 set,并报告丢弃。
## 它是所有属性设置路径共享的单一事实源(SSOT):编辑器
## 节点/场景处理器(通过 [code]commands/editor_helpers.gd[/code])以及
## 运行时自动加载的 [code]runtime.set_property[/code](通过直接 preload)。
##
## 设计上即为运行时干净 — 它只引用值类型、[String] 格式化
## 与 [@GlobalScope] 函数([method @GlobalScope.type_string]、
## [method @GlobalScope.is_equal_approx]),从不引用编辑器类,
## 且不 preload 任何东西。此静态图中任何位置出现编辑器符号
## 都会使运行时自动加载在未剥离的导出中解析失败
## (godotengine/godot#91713),且运行时服务器会 preload 此文件;请保持它无编辑器引用。


## 将属性写入分类为:干净 / 被接受但被调整 / 静默丢弃。
## Godot 的 [method Object.set] 是 void:它会静默丢弃类型错误的 Variant
## (虚假成功),但也会静默重塑可转换的 Variant(float 7.9 →
## int 7、"Foo/" → "Foo"、归一化方向)— 被接受,但存储的值
## 与请求的不同。用前后回读包围一次 set,此函数
## 返回三态,使调用方可以干净成功、带警告成功或
## 失败。编辑器节点/场景路径与运行时自动加载共享。
## [br]
##   [param before]  设置之前属性的值(其类型是判定基准)
##   [param after]   设置之后属性的值(从同一位置读取)
##   [param coerced] 被赋的值(已从 JSON 强制转换)
##   [param path]    属性路径,用于消息
## [br]
## 返回三态字典:
##   [code]{"status": "ok"}[/code] — 存储 ≈ 请求(精确设置、int→float 且
##       5.0 == 5、设置为相同);干净成功。
##   [code]{"status": "adjusted", "warning": String}[/code] — 写入被接受,
##       但引擎重塑了它(截断/净化/钳制/归一化),因此存储
##       值 ≠ 请求;成功 + 一条指明属性/请求值/存储值的警告。
##   [code]{"status": "dropped", "error": String}[/code] — 静默的类型错误丢弃
##       (跨家族、值未变、≠ 请求)→ 调用方发出 SET_FAILED。
static func describe_set_drop(
	before: Variant, after: Variant, coerced: Variant, path: String,
) -> Dictionary:
	# 报告无错误但什么都没有:该属性不存在于此对象上。
	# 提示以最常见的构建流程原因为开头 — 在脚本尚未附加时
	# 设置脚本定义的属性 — 然后是名称拼写错误与专用 API 的情况。
	if after == null and coerced != null:
		return {"status": "dropped", "error":
			"set() on '%s' reported no error but readback is null. " % path
			+ "The property is not present on this object — most often it is defined "
			+ "by a script not attached to this node yet (attach the script first, "
			+ "then set its properties), a mistyped property name, or a property that "
			+ "needs a dedicated API (e.g. set_shader_parameter, add_animation_library)."}
	# 存储值等于请求 → 干净成功(精确、int→float 5.0==5、设置为相同)。
	if _values_loosely_equal(after, coerced):
		return {"status": "ok"}
	# 存储值 ≠ 请求。可转换家族门控是区分重塑与丢弃的
	# 唯一裁决者,对值已变与值未变两条路径都适用:
	#   • 同家族 → 引擎合理地重塑了家族内的值
	#     (截断 7.9→7、净化 "Foo/"→"Foo"、归一化方向)→ 已调整。
	#   • 跨家族 → 引擎没有存储真实的值。它要么保留旧值
	#     (纯丢弃,after == before),要么 — 对于像 position /
	#     modulate 这样的绑定设置器(setter)— 将错误类型 Variant 转换为 ZERO
	#     并存储它,摧毁了先前的值(after ≠ before)。两者都是丢弃,
	#     而非重塑。按家族门控(而不是按 after == before)
	#     正是捕获这个由非零先前值暴露的破坏性归零情况的关键。
	if before != null and _same_convertible_family(typeof(coerced), typeof(before)):
		return {"status": "adjusted", "warning": _adjusted_warning(after, coerced, path)}
	# 跨家族 → 写入没有作为真实值落地(保留旧值或归零)。
	return {"status": "dropped", "error":
		"property '%s' rejected the value: expected type %s, got %s (%s). "
		% [path, type_string(typeof(before)), type_string(typeof(coerced)),
			_brief_set_value(coerced)]
		+ "Godot's set() silently discards a wrong-type value. Pass a value of the "
		+ "expected type — for struct types use the tagged form "
		+ "(e.g. {\"type\":\"Vector2\",\"x\":0,\"y\":0})."}


## 被接受但被调整的写入的警告文本,指明存储了什么与请求了什么。
static func _adjusted_warning(after: Variant, coerced: Variant, path: String) -> String:
	return ("note: '%s' stored %s but you requested %s — the engine adjusted the value to fit %s."
		% [path, _brief_set_value(after), _brief_set_value(coerced), type_string(typeof(after))])


## 当将类型 [param t_coerced] 赋值给类型为 [param t_before] 的属性时,
## 若处于 Godot 的 set() 接受并就地重塑的同一可转换家族内,则返回 true:
## 完全相同类型(值类型在设置时可能归一化/变换)、
## 均为数值(bool/int/float — 引擎会截断 float→int 等),或
## 均为类字符串(String/StringName/NodePath — 归一化/净化)。OBJECT
## 被排除 — Resource 赋值没有家族内重塑,因此错误子类型的丢弃
## 必须交由同一性兜底捕获,而不是在此处被信任。
static func _same_convertible_family(t_coerced: int, t_before: int) -> bool:
	if t_coerced == TYPE_OBJECT or t_before == TYPE_OBJECT:
		return false
	if t_coerced == t_before:
		return true
	if _is_numeric_type(t_coerced) and _is_numeric_type(t_before):
		return true
	if _is_stringy_type(t_coerced) and _is_stringy_type(t_before):
		return true
	# 整数/浮点向量变体类型也会转换(Vector2 7.9 → Vector2i 7),与
	# _values_loosely_equal 保持一致,因此此处对当前值截断会判定为已调整,而非丢弃。
	if (t_coerced == TYPE_VECTOR2 or t_coerced == TYPE_VECTOR2I) \
			and (t_before == TYPE_VECTOR2 or t_before == TYPE_VECTOR2I):
		return true
	if (t_coerced == TYPE_VECTOR3 or t_coerced == TYPE_VECTOR3I) \
			and (t_before == TYPE_VECTOR3 or t_before == TYPE_VECTOR3I):
		return true
	return false


## 当 [param t] 是数值型 Variant 类型时为 true。bool 也算 — 引擎在数值
## 槽位中将其赋值为 0/1,因此 bool↔int/float 写入是真实转换。
static func _is_numeric_type(t: int) -> bool:
	return t == TYPE_BOOL or t == TYPE_INT or t == TYPE_FLOAT


## 当 [param t] 是类字符串 Variant 类型时为 true。String、StringName 与
## NodePath 在引擎中可互相赋值(set() 会在它们之间转换)。
static func _is_stringy_type(t: int) -> bool:
	return t == TYPE_STRING or t == TYPE_STRING_NAME or t == TYPE_NODE_PATH


## 用于判断写入是否落地的跨类型宽容值相等比较。相同
## 类型 → 精确 [code]==[/code]。在数值家族(bool/int/float)与
## 字符串家族(String/StringName/NodePath)内,值跨家族比较;
## 整数/浮点向量变体类型(Vector2i↔Vector2、Vector3i↔Vector3)进行
## 近似比较 — 各自对应 Godot 的 set() 执行的一种转换。
## 其他跨类型情况一律不相等。
static func _values_loosely_equal(a: Variant, b: Variant) -> bool:
	var ta := typeof(a)
	var tb := typeof(b)
	if ta == tb:
		return a == b
	if _is_numeric_type(ta) and _is_numeric_type(tb):
		return is_equal_approx(float(a), float(b))
	if _is_stringy_type(ta) and _is_stringy_type(tb):
		return str(a) == str(b)
	if (ta == TYPE_VECTOR2 or ta == TYPE_VECTOR2I) \
			and (tb == TYPE_VECTOR2 or tb == TYPE_VECTOR2I):
		var v2a: Vector2 = type_convert(a, TYPE_VECTOR2)
		var v2b: Vector2 = type_convert(b, TYPE_VECTOR2)
		return v2a.is_equal_approx(v2b)
	if (ta == TYPE_VECTOR3 or ta == TYPE_VECTOR3I) \
			and (tb == TYPE_VECTOR3 or tb == TYPE_VECTOR3I):
		var v3a: Vector3 = type_convert(a, TYPE_VECTOR3)
		var v3b: Vector3 = type_convert(b, TYPE_VECTOR3)
		return v3a.is_equal_approx(v3b)
	return false


## 为错误消息提供的简短、安全的值的渲染(在 60 字符处截断)。
static func _brief_set_value(value: Variant) -> String:
	var s := var_to_str(value)
	if s.length() > 60:
		return s.left(57) + "..."
	return s
