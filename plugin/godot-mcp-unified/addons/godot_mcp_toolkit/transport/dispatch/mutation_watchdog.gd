@tool
extends RefCounted
## 当执行中的变更协程中止或永不完成时,恢复变更派发锁 — 唯一能防止一次卡死
## 的变更永久阻塞所有变更的安全网。它持有执行中身份(peer/id/method/ctx)、
## 自适应期限,以及让卡死协程的尾段在最终恢复时放弃的代计数器;变更通道
## (dispatch_lane.gd)持有单飞标志 + FIFO 队列。两者通过这个微小接口对话:
## 执行开始时 arm(),正常完成时 disarm(),每帧 tick(),通道的尾段守卫用
## current_generation(),以及触发时调用以恢复通道锁的注入式 force_clear
## Callable。
##
## 为什么与通道拆分:看门狗是唯一必须无条件地每个 _process 帧 tick 的部件,
## 独立于轮询节奏与通道状态 — 与通道的队列/执行中簿记是不同的生命周期节奏。
## 拆分它得到一个干净、可无头单元测试的"计时器+代计数"子模块。
##
## 仅编辑器侧(运行时自动加载没有变更通道 → 绝不 preload 本文件),因此
## #91713 在这里不适用 — 但出于单一职责仍保持为"干净的"纯逻辑子模块:
## 它不引用任何 Editor* 符号。唯一伸回编辑器服务器的途径是注入的 force_clear
## Callable;其余全部是核心类(WebSocketPeer/Time/Callable),加上共享纯净的
## Notifier 与 MCPToolkitToolContext 值类型。期限"值"由通道计算(它持有注册表
## 与宽限 ProjectSetting)并传入 arm();这个子模块只存储它,并判断何时触发。

const Notifier := preload("res://addons/godot_mcp_toolkit/transport/notifier.gd")

# 注入的恢复钩子:func(trapped_peer, trapped_id) -> void。在触发时"最后"调用,
# 此时本子模块已递增代计数并清除自己的身份,用于恢复通道:擦除被困请求的
# 活动上下文(按 peer 限定的键需要身份的两半)、清除单飞标志,并排水变更
# 队列。排水会同步地重新武装后继者,因此它必须在本子模块已把自己的身份置空
# 之后运行(否则排水触发的全新 arm() 会被践踏)。
var _force_clear: Callable = Callable()

# 有变更在执行时为 true(与通道的 _mutation_in_flight 完全一致:
# 在 arm() 中置位,在 disarm() 与触发时清除)。在这里跟踪,让 tick()
# 自洽,永远不必伸入通道读取该标志。
var _armed := false
# 执行中身份 + 自适应期限,由 arm() 在变更的第一个 await 之前同步盖章
# (因此期限只跟踪执行中的时间,绝不包含排队等待,也不会产生竞态)。
var _started_ms := 0
var _deadline_ms := 0
var _peer: WebSocketPeer = null
var _id  # int 或 null — 执行中变更的 JSON-RPC id
var _method := ""
# 可取消命令的上下文,予以跟踪,让触发时能协作地取消一个"缓慢但存活"的
# 处理器(会轮询 ctx.is_cancelled() 的那种)。
var _ctx: MCPToolkitToolContext = null
# 每次触发时递增,让被强制清除的协程(如果它最终恢复)经 current_generation()
# 看到代不匹配,并放弃自己的整段尾段。
var _generation := 0


## 接线触发时调用的恢复钩子,用于恢复通道锁。在第一次 arm() 之前、构造时
## 调用一次(由变更通道调用)。
func set_force_clear(force_clear: Callable) -> void:
	_force_clear = force_clear


## 把一个变更标记为执行中:记录其身份 + 预先计算好的期限,并返回当前代计数,
## 供通道捕获作为其尾段守卫。在执行开始时、变更的第一个 await 之前同步调用。
## 通道传入它计算好的期限(开始时间 + 命令的看门狗超时 + 宽限)—
## 本子模块不认识注册表或宽限设置。
func arm(peer: WebSocketPeer, id, method: String, started_ms: int,
		deadline_ms: int, ctx: MCPToolkitToolContext) -> int:
	_armed = true
	_peer = peer
	_id = id
	_method = method
	_started_ms = started_ms
	_deadline_ms = deadline_ms
	_ctx = ctx
	return _generation


## 正常完成时清除执行中身份。不递增代计数(没有发生放弃),也不触碰通道的
## 单飞标志(那归通道所有)。与通道在同一点把 _mutation_in_flight 置为 false
## 保持一致。
func disarm() -> void:
	_armed = false
	_peer = null
	_ctx = null


## 当前代计数器 — 通道在 arm() 时捕获它,并在处理器 await 之后重读,以检测
## await 中途的强制清除(代不匹配 → 放弃尾段)。
func current_generation() -> int:
	return _generation


## 若执行中的变更已超过其期限,则恢复变更锁。无条件地每个 _process 帧运行 —
## 它是卡死锁的唯一恢复手段,因此绝不能被轮询节奏或租约状态门控。期限是
## 自适应的,并在执行开始时盖章,因此合法缓慢的变更(哪怕打满上限的扩展)
## 绝不会触发它;只有已中止/永不完成的变更才会。一次触发意味着 C1 保存重入
## 防护未能阻止卡死,因此要响亮地警告。
func tick() -> void:
	if not _armed:
		return
	if Time.get_ticks_msec() <= _deadline_ms:
		return
	push_warning(("[MCPToolkit] mutation watchdog: '%s' (id %s) exceeded its "
		+ "deadline (%d ms in flight) - force-clearing the dispatch lock. If this "
		+ "recurs, the save-reentrancy guard (C1) is not holding.") % [
			_method, str(_id), Time.get_ticks_msec() - _started_ms])
	if _peer != null and _peer.get_ready_state() == WebSocketPeer.STATE_OPEN:
		Notifier.send_error(_peer, _id, -32000,
			"mutation watchdog timeout — the editor did not complete the operation in time")
	# 协作地取消执行中的处理器(若可取消且仍存活):会轮询 ctx.is_cancelled()
	# 的处理器在下次检查时退出,缩小"缓慢但存活"的变更与看门狗启动的后继者
	# 并发运行的窗口。挂死的处理器会忽略它(无害)。
	if _ctx != null:
		_ctx.cancel()
	# 先递增代计数,让卡死的协程(如果它恢复)跳过自己的整段尾段
	# (通道在 _execute_mutation 中的代守卫)。
	_generation += 1
	# 在置空之前捕获身份 — force_clear 需要 peer + id 来擦除被困请求的
	# 按 peer 限定的活动上下文。
	var trapped_peer := _peer
	var trapped_id = _id
	# 在 force_clear 之前先把自己的身份置空:force_clear 会排水队列,从而
	# 同步地重新武装后继者;先在这里清除,意味着那个全新的 arm() 绝不会被
	# 践踏。
	_armed = false
	_peer = null
	_ctx = null
	# 最后恢复通道(擦除活动上下文 + 清除单飞标志 + 排水)。
	if _force_clear.is_valid():
		_force_clear.call(trapped_peer, trapped_id)
