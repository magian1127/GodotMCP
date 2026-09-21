@tool
extends RefCounted
## 构造并向对端发送 JSON-RPC 的 result / error / notification 帧,并按对端的
## 发送缓冲对结果做尺寸防护;向给定的一组已鉴权对端广播通知。由编辑器
## 服务器(模式 A)与运行时自动加载(模式 B)共享,因此保持对导出友好 —
## 不含编辑器符号,只有 WebSocketPeer / JSON / MCPToolkitError。

const JSONRPC_VERSION := "2.0"


## 发送 JSON-RPC result 帧,并针对对端的出站缓冲做防护。超过该缓冲的响应
## 会被原生 WS 路径整体拒收(没有分块)— 没有防护的话它会无声消失,桥接
## 只会看到一个挂起的请求。guard_response_size 把它换成一个紧凑、可投递的
## RESPONSE_TOO_LARGE 错误,让调用方学会收窄/分页。max_bytes 是对端自己的
## 缓冲(在接受连接时设置),因此这里不需要读取 ProjectSetting。
## log_prefix 用调用服务器的名字标记发送失败的警告。
static func send_result(peer: WebSocketPeer, id, result, log_prefix: String) -> void:
	var response := {
		"jsonrpc": JSONRPC_VERSION,
		"id": id,
		"result": result,
	}
	response = MCPToolkitError.guard_response_size(response, peer.outbound_buffer_size)
	var send_err := peer.send_text(JSON.stringify(response))
	if send_err != OK:
		push_warning("%s send_text failed for id %s (err %d) - response not delivered" % [log_prefix, str(id), send_err])


## 发送 JSON-RPC error 帧。不做尺寸防护:错误帧从构造上就是紧凑的,
## 没有什么可截断。
static func send_error(peer: WebSocketPeer, id, code: int, error_message: String) -> void:
	var response := {
		"jsonrpc": JSONRPC_VERSION,
		"id": id,
		"error": {
			"code": code,
			"message": error_message,
		},
	}
	peer.send_text(JSON.stringify(response))


## 向单个对端发送 JSON-RPC notification 帧(无 id),跳过非 OPEN 的对端。
## 通知不携带请求 id,因此过大的帧无法用错误应答 — 至少要把发送失败暴露
## 出来,确保超出缓冲的通知绝不会无声丢弃。log_prefix 标记该警告。
static func send_notification(peer: WebSocketPeer, method: String, params: Dictionary, log_prefix: String) -> void:
	if peer.get_ready_state() != WebSocketPeer.STATE_OPEN:
		return
	var send_err := peer.send_text(JSON.stringify({
		"jsonrpc": JSONRPC_VERSION,
		"method": method,
		"params": params,
	}))
	if send_err != OK:
		push_warning("%s notification '%s' send_text failed (err %d)" % [log_prefix, method, send_err])


## 向 authed_peers 中每个 OPEN 的对端广播配置变更通知,不携带请求 id
## (调用方是重载自己的工具列表,而不是应答请求)。使用停靠面板广播信封
## ({notification, params?}),不同于按请求的 send_notification 信封。返回
## 成功发送帧的对端数量,便于调用方记录摘要。过大的帧同样无法用错误应答 —
## 要暴露发送失败,让丢帧可见。
static func broadcast(authed_peers: Array, notification_type: String, params: Dictionary, log_prefix: String) -> int:
	var payload := {"notification": notification_type}
	if not params.is_empty():
		payload["params"] = params
	var message := JSON.stringify(payload)
	var count := 0
	for peer in authed_peers:
		if peer is WebSocketPeer and peer.get_ready_state() == WebSocketPeer.STATE_OPEN:
			# 强转:循环变量是 Variant(调用方提供的 Array 元素),因此调用需要
			# 带类型的接收者才能拿到 Error 返回值。
			var send_err := (peer as WebSocketPeer).send_text(message)
			if send_err != OK:
				push_warning("%s broadcast '%s' send_text failed (err %d)" % [log_prefix, notification_type, send_err])
			count += 1
	return count
