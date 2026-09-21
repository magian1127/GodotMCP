@tool
extends RefCounted
## 针对“陈旧活动实例方法调用”隐患的纯判定 + 消息辅助函数。
## 在 Godot < 4.4 上,已在运行某脚本的活动实例看不到对该脚本的修改 ——
## 无论是新增成员还是改动过的方法体 —— 直到编辑器重启为止:
## editor.refresh、重新附加脚本、甚至全新创建一个节点,都仍然保留
## 旧代码。4.4+ 在带显示的编辑器上会立即热重载,因此那里无需提示 —— 但
## 无头(headless)的 4.4+ 编辑器从不重新实例化活动节点,所以陈旧提示
## 在那里同样会触发,只是改用无头场景专属的措辞。
##
## 已在 4.2.0 / 4.3.0 / 4.4.1 / 4.5.0 / 4.6.2 上实证刻画(分界点为
## 4.3 -> 4.4)。该刻画还证明了全新节点在
## 4.2 和 4.3 上同样陈旧,因此两者归并为同一种恢复方式(重启编辑器)——
## 不存在 4.2 与 4.3 的分支差异。
##
## 拆分(与 unfocused_backup.gd 一致):这里的每个函数都是纯函数,且在无头模式下可用 ——
## 判定谓词把版本 `major`/`minor` 与 `headless` 标志当作
## 数据传入,因此 run_unit_tests.gd 无需编辑器即可覆盖 major/minor/headless
## 各种组合下的每个分支(4.2-4.6 + 一个 5.0 守卫)。与编辑器耦合的
## 调用方读取运行中的版本 / 磁盘上的源码,再喂给这些函数:
##   - script_commands.gd  :对已有 .gd 执行 script.write 时的主动提示
##   - node_commands.gd    :node.call_method -> INVALID_METHOD 时的被动提示
##
## Godot 4.x 是受支持的版本范围;门控条件为 `major == 4 and (minor < 4 or headless)`,
## 因此 4.0-4.3 的热重载隐患总是命中,4.4+ 的隐患仅在无头模式下命中,
## 而任何未来的 5.x 都会正确跳过。与编辑器耦合的调用方从
## `Engine.get_version_info()` / `DisplayServer` 取出
## `major`、`minor` 与无头标志喂入。

const _RECOVERY := (
	"On Godot %s, a live instance already running this script keeps the OLD code: "
	+ "changed method bodies AND newly-added members stay invisible to it. "
	+ "editor.refresh, re-attaching the script, and even creating a fresh node do NOT "
	+ "pick up the edit on Godot < 4.4 — relaunch the editor (or disable then re-enable "
	+ "the plugin) before calling the changed or added members."
)

# Godot 4.4+ 无头模式:带显示的编辑器会立即热重载活动实例,但无头编辑器
# 从不重新实例化它们 —— 这与 < 4.4 的引擎缓存陈旧是两种不同的隐患,
# 因此它带有自己的恢复指引(恢复方式是重建节点或重启编辑器,
# 而绝不是 editor.refresh)。
const _RECOVERY_HEADLESS := (
	"On Godot %s, the live instance running this script is stale — "
	+ "headless editors don't re-instantiate live nodes on reload — re-create the node "
	+ "or relaunch a display editor; the edit is on disk, confirm with script_check."
)

const _WRITE_PREFIX := (
	"Validate scripts with script_check or lsp_diagnostics (errors also surface in "
	+ "log_read(channel:'editor')). Then note: "
)


## 主动触发:一个已存在的 .gd 被重新写入,且编译通过,且
## 编辑器为 Godot < 4.4。(新建 / 4.4+ / 编译失败 / 非 .gd -> 不提示。)
static func should_warn_on_write(existed: bool, compiled_ok: bool, extension: String, major: int, minor: int) -> bool:
	return existed and compiled_ok and extension == "gd" and major == 4 and minor < 4


## 被动触发:node.call_method 在某个由 .gd 脚本驱动的节点上命中 INVALID_METHOD
## (has_method 为 false),而该节点的磁盘源码定义了该方法且能编译,同时
## 活动实例已陈旧。两种陈旧情形:任何模式下的 Godot < 4.4(编辑器从不
## 热重载活动实例),或 [param headless] 下的 Godot 4.4+(带显示的编辑器
## 会热重载 —— 那样 has_method 本应为真 —— 但无头编辑器从不
## 重新实例化重载后的节点)。磁盘上没有该方法 -> 是拼写错误,不提示。
## 磁盘源码无法编译 -> 真正要修的是编译错误(方案 B),不给陈旧提示。
## 4.4+ 且有显示 -> 不陈旧,因此本函数从不触发。
static func should_hint_on_call(
	has_method: bool, disk_has_method: bool, disk_compiles: bool, is_gd: bool,
	major: int, minor: int, headless: bool = false,
) -> bool:
	if not ((not has_method) and disk_has_method and disk_compiles and is_gd and major == 4):
		return false
	return minor < 4 or headless


## 恢复指引,按陈旧情形定制。[param ver_label] 是检测到的
## "major.minor",让消息能点名实际运行的版本。当 [param headless] 为真且
## 引擎为 4.4+([param minor] >= 4)时,返回无头重实例化形式的指引;
## 否则返回 < 4.4 引擎缓存形式的指引(4.2 与 4.3 都需要重启编辑器;
## 全新节点对两者都无济于事)。默认参数保持单参数的 < 4.4 调用方
## (write_hint)不变。
static func recovery_message(ver_label: String, minor: int = -1, headless: bool = false) -> String:
	if headless and minor >= 4:
		return _RECOVERY_HEADLESS % ver_label
	return _RECOVERY % ver_label


## 为一次成功的 script.write 组装提示(刻意的排序:校验指引在前,
## 情境性的陈旧提醒占据“最新消息”的位置)。
static func write_hint(ver_label: String) -> String:
	return _WRITE_PREFIX + recovery_message(ver_label)


## `source` 能作为 GDScript 解析/编译时为真。通过 GDScript.new().reload()
## 进行安全的进程内解析 —— 而不是 ResourceLoader.load(CACHE_MODE_IGNORE),
## 后者在所有版本上都会损坏已加载的脚本(P-056)。会先把 class_name 行
## 置空,以避免全局类重复注册造成的误报(P-053)。
static func source_compiles(source: String) -> bool:
	var lines := source.split("\n")
	for i in lines.size():
		if lines[i].strip_edges().begins_with("class_name "):
			lines[i] = ""
			break
	var script := GDScript.new()
	script.source_code = "\n".join(lines)
	return script.reload(false) == OK


## `source` 中定义了 `func <method>` 时为真(对原始文本逐行扫描 —— 不编译,
## 不用正则标志)。匹配 `func name(`、`static func name (` 以及带缩进的
## 内部类方法;忽略出现在字符串/注释里的 `func`,因为剥离后的行必须
## 以 `func ` / `static func ` 开头。
static func source_has_method(source: String, method: String) -> bool:
	if method.is_empty():
		return false
	for raw_line in source.split("\n"):
		var line := raw_line.strip_edges()
		if not (line.begins_with("func ") or line.begins_with("static func ")):
			continue
		var after := line.substr(line.find("func ") + 5).strip_edges()
		var paren := after.find("(")
		if paren == -1:
			continue
		if after.substr(0, paren).strip_edges() == method:
			return true
	return false
