@tool
extends RefCounted
## 在 ProjectSettings 中注册与移除工具集的运行时自动加载(Autoload)—— 它是
## 首次启用与加载时自愈背后的共用路径,因此二者永远不会
## 产生分歧。
##
## 特意使用 ProjectSettings.set_setting + save 而非 EditorPlugin.add_autoload_singleton
## 来写入自动加载(Autoload)。add_autoload_singleton 只会更新
## 编辑器的内存状态并推入一条启动撤销记录,而游戏在按 F5 时读取的是 project.godot:
## 直接写入路径在构造上即持久化到磁盘(得以保留进构建产物),且不会留下撤销记录
## (误按 Ctrl+Z 也撤销不了注册)。has_setting 守卫意味着已存在的值永远不会被覆盖,
## 因此健康的项目不会发生任何写入,project.godot 也不会出现差异。
## 移除则保持不对称 —— [method unregister] 使用 remove_autoload_singleton,
## 这是一项真正的编辑器操作,无需磁盘持久化
## (这里关心的是恢复一个缺失的自动加载(Autoload),
## 而不是撤销启用开关)。
##
## 仅限编辑器:接收一个 EditorPlugin 并驱动 ProjectSettings;它只被仅限编辑器的
## plugin.gd 预加载,绝不会被运行时自动加载(Autoload)预加载。自动加载(Autoload)身份
## (名称/路径对 + 键/值推导)来自共享的 autoload_identity
## 叶子模块,因此导出剥离域读取的是同一来源,而无需
## 与本模块耦合。不使用 [code]class_name[/code]:使用方以
## [code]const AutoloadRegistration := preload(...)[/code] 的方式访问它。

const Identity := preload("res://addons/godot_mcp_toolkit/core/autoload_identity.gd")


## 保证所有必需的自动加载(Autoload)都存在于 ProjectSettings 中,
## 且只写入缺失的部分。
##
## 逐一探测每个必需条目,用 set_setting 写入缺失的条目,然后 —— 在循环结束后
## 执行一次、且仅当确实写入过内容时 —— 调用 save()(持久化,供下次
## F5 生效)并发出 settings_changed(在磁盘写入后刷新编辑器内存中的自动加载(Autoload)视图)。
## 必须避免过早触发 settings_changed 的调用方,
## 应当在接好自己的 settings_changed 监听器之前调用本方法。
static func ensure_registered() -> void:
	var present: PackedStringArray = PackedStringArray()
	for entry in Identity.REQUIRED_AUTOLOADS:
		var autoload_name: String = entry[0]
		var key := Identity.settings_key(autoload_name)
		if ProjectSettings.has_setting(key):
			present.append(key)

	var missing: Array = compute_missing(present, Identity.REQUIRED_AUTOLOADS)
	for entry in missing:
		var autoload_name: String = entry[0]
		var script_path: String = entry[1]
		ProjectSettings.set_setting(
				Identity.settings_key(autoload_name), Identity.settings_value(script_path))

	if not missing.is_empty():
		ProjectSettings.save()
		ProjectSettings.emit_signal("settings_changed")


## 通过 [param plugin] 的 remove_autoload_singleton 移除必需的自动加载(Autoload)。
## 这是启用路径在拆除阶段的对应操作;移除是一项真正的编辑器操作,
## 无需磁盘持久化,因此与注册刻意保持不对称。
static func unregister(plugin: EditorPlugin) -> void:
	for entry in Identity.REQUIRED_AUTOLOADS:
		var autoload_name: String = entry[0]
		plugin.remove_autoload_singleton(autoload_name)


## 纯决策:对 [param required] 中的 [name, path] 对,返回其 "autoload/<name>" 键
## 不在 [param present](已探测到的、当前存在的自动加载(Autoload)键集合)中的
## 那些条目。
##
## 纯数据进、纯数据出 —— 带副作用的外壳负责 ProjectSettings
## 的探测与写入,因此本函数在无头(headless)模式下仍可单元测试。保持为普通静态方法
## (不用 Callable),这样裸静态方法引用就不会触到 Godot 4.2 对未绑定静态 Callable
## 的 NIL-self 绑定问题。
static func compute_missing(present: PackedStringArray, required: Array) -> Array:
	var missing: Array = []
	for entry in required:
		var autoload_name: String = entry[0]
		if not present.has(Identity.settings_key(autoload_name)):
			missing.append(entry)
	return missing
