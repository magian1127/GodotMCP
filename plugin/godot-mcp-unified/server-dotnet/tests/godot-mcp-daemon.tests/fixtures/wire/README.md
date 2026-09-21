# Wire-contract fixture 语料（v1）

Node 桥 ↔ Godot addon 之间线上契约的**可重放语料**（issue 02）。它是三方的共同真源：

- **Node 桥契约回归**：`test/unit/wireFixtures.test.ts` 用真实的 `createChannel` 对这些 fixture 全量回放，随 `npm run test:unit`（及 `npm run verify`）持续保持 green；
- **C# daemon 通道实现**（issue 05）：行为依据；
- **fake Godot 测试替身**（issue 04）：应答脚本来源。

## 契约基线（真源从哪来）

- 契约清单：`server/docs/architecture/README.md` §15（C1–C22）。
- 权威代码（形状以代码为准，不以文档叙述为准）：
  - toolkit 侧：`addons/godot_mcp_toolkit/transport/**`（`server_request_router.gd` 文件头声明**线路契约是冻结的**：通知方法 `_queued`/`_executing`/`_cancel`/`echo`、JSON-RPC 错误码、信封键不得改名改形）、`contract/**`（信封/错误码/强制转换）。
  - Node 侧：`server/src/transport/**`、`server/src/shared/errorContract.ts`。
- 引擎版本：帧形状在 Godot 4.2–4.7 间稳定（版本差异见 `addons/godot_mcp_toolkit/docs/compatibility.md`；版本门控只影响工具可用性，不影响帧结构）。
- **provenance.kind = `derived`**：本语料自两侧代码与架构文档推导而成，**未经真机录制**；`godot_version`/`version` 等为示例值。后续如需可增补 `recorded`（真机抓包）条目，schema 不变。

## 文件格式（schema `wire-fixture/1`）

```jsonc
{
  "schema": "wire-fixture/1",
  "id": "auth-editor-happy",              // 唯一；与文件名一致
  "title": "人类可读标题",
  "contract": ["C1", "C2"],               // 覆盖的契约条款
  "provenance": {
    "kind": "derived",
    "sources": ["<repo 相对路径>#<符号>"],
    "engineBasis": "…"
  },
  "auth": { "ack": { … } } ,              // 或 { "close": { "code": 1008, "reason": "…" } }
  "channelOpts": { "noReconnect": true, "skipVersionCheck": true },  // 可选，透传 createChannel
  "onConnect": [ { "afterMs": 30, "send": { …帧… } } ],              // 可选：鉴权 ack 后主动推送
  "steps": [ … ],                          // 见下
  "expectAuthResolved": { "version": "4.5.1", "headless": false },   // 可选；null = 断言未被调用
  "expectNotifications": [ { "type": "…", "params": { … } } ]        // 可选；全量深比较
}
```

### steps 三种形态

```jsonc
// 1) 单次调用（触发一次 channel.call；script = 服务端在该请求到达后要发送的帧序）
{
  "name": "…",
  "call": { "method": "scene.get_tree", "params": { }, "timeoutMs": 3000, "abortAfterMs": 60 },
  "script": [ { "afterMs": 0, "send": { …帧… } } ],
  "expect": { "resolves": { …精确值… } },            // 或 { "rejects": { "code": "…", "messageIncludes": "…" } }
  "expectSent": { "jsonrpc": "2.0", "method": "…", "params": { … } },  // 断言客户端实际发出的帧（id 另行断言为非空字符串）
  "expectCancelFrame": true                          // 断言收到的最后两帧 = 该请求 + 随后的 _cancel（request_id 指向它）
}

// 2) 并发调用（用于“被串行化的变更”等时序场景；A 先发，B 按 afterMs 后发）
{
  "name": "…",
  "concurrentCalls": [
    { "label": "A", "call": { … }, "script": [ … ] },
    { "label": "B", "afterMs": 80, "call": { … }, "script": [ … ] }
  ],
  "expect": { "A": { "resolves": … }, "B": { "resolves": … } },
  "expectSent": { "A": { … }, "B": { … } }           // 可选
}

// 3) 纯等待（给 onConnect 推送留到达时间）
{ "sleepMs": 100 }
```

### 占位符与匹配器

- 占位符（在 `send` 帧内替换）：`$request_id`（触发该 script 的请求 id）、`$token`、`$server_version`。
- 匹配器（在 `expect` 值内使用）：
  - `{ "$regex": "<pattern>" }`：目标为字符串且整体可被 `new RegExp(pattern)` 命中；
  - `{ "$untrusted": { "kind": "…", "source": "…", "bodyJson": { … } } }`：目标为 untrusted 信封字符串，剥壳后 kind/source 相等、正文 `JSON.parse` 后与 `bodyJson` 深相等。
- 断言为**严格深比较**（期望对象的键集合即实际键集合），用于钉住形状。

## 消费接口

### A. Node 回放（契约回归基线）

`npm run test:unit` 自动发现 `wireFixtures.test.ts`：以真实 `createChannel` 直连语料驱动的 mock WS server，逐 fixture 回放并断言三条线：客户端发出帧、对话结局、通知送达。新增/修改 fixture 后同一命令验证。

### B. fake Godot（issue 04）与 C# 通道实现（issue 05）

把一个 fixture 读成**应答脚本**：

1. 接受连接后：等首帧 `{auth, version}` → 按 `auth.ack` 回复（或按 `auth.close` 关闭）；
2. 之后每收到一条请求帧（`id != null`、带 `method`），取该 fixture 中**首个尚未消费**、`method` 匹配的调用步骤，把其 `script` 帧中的 `$request_id` 替换为收到的 `id` 后按 `afterMs` 顺序发出；
3. `onConnect` 帧在 ack 之后立即调度；
4. 通知帧（`{notification, params?}` / `{method: "_queued"|"_executing", …}`）不需回应。

权威消费样例即 `test/unit/wireFixtures.harness.ts`（约 250 行），C# 侧照此实现即可。

## 服务端行为基线（不可由 Node 客户端回放；供 fake Godot / 05 号实现）

以下帧由 Godot 侧产生、Node 侧无法主动触发，故以示例固化于此（出处均为 toolkit 代码）：

- **解析错误**（未鉴权也触发——解析先于鉴权，见 `mcp_server.gd` `_handle_message`）：
  - 非法 JSON → `{ "jsonrpc": "2.0", "id": null, "error": { "code": -32700, "message": "Parse error: …" } }`
  - 顶层非对象 → `{ "jsonrpc": "2.0", "id": null, "error": { "code": -32600, "message": "Invalid Request: top-level must be an object" } }`
- **id 强制转换**（`server_request_router.gd` `route_request`）：Godot JSON 把所有数字解析为 float，整数值 float 回写为 int——`{"id": 1}` 往返后仍是 `1` 而非 `1.0`。
- **鉴权超时**（`ws_transport.gd` `poll_peers`）：2 秒内无有效鉴权 → WS close `1008 "auth timeout"`；错误 token → close `1008 "invalid token"`。
- **`echo`**：传输层诊断请求，`result` = 原样回送 `params`（路由注释明确其非领域命令）。
- **广播通知类型**（`{notification, params?}` 信封）当前全集：`extensions.changed`（`params = {commands: [entry…], removed: [method…]}`，entry 形状见 `extension_meta_commands.gd` `build_command_entry`）、`game_stopped`（无 params）。
- **RESPONSE_TOO_LARGE 尺寸护栏**（`contract/mcp_toolkit_error.gd` `guard_response_size`）：结果超过对端出站缓冲时，`result` 被替换为紧凑失败信封（携带恢复 hint；截图载荷走 `image_response_mode:"disk"` 专用 hint）。

## fixture 清单

| 文件 | 契约 | 要点 |
| --- | --- | --- |
| `auth-editor-happy.json` | C1 C2 | 首帧 `{auth,version}` → 编辑器 ack（含 `godot_version`/`version`/`headless`） |
| `auth-runtime-bare-ack.json` | C2 | 运行时 ack 仅 `{authed:true}`；`noReconnect`+`skipVersionCheck` 通道 |
| `auth-rejected-invalid-token.json` | C2 | 错误 token → 1008 关闭 → 客户端 `AUTH_FAILED` |
| `echo-roundtrip.json` | C1 | 嵌套 params 原样回送；请求帧四键钉形 |
| `read-scene-get-tree-untrusted.json` | C1 C3 C8 C21 | 只读通道无排队通知；结果携带 untrusted 信封 |
| `mutation-single-scene-create-node-created.json` | C3 C5 C8 | 变更执行前 `_executing`；`status:"created"` |
| `mutation-serialized-queued-executing.json` | C3 C5 C6 C8 | A 在飞、B 被串行化：`_queued`→（A 完成）→`_executing`；B 为幂等 `status:"returned"` |
| `cancel-inflight-request.json` | C5 | abort → 客户端 `CANCELLED` + `_cancel` 通知帧 |
| `error-rpc-method-not-found.json` | C4 | JSON-RPC `-32601` error 帧 → 客户端 `RPC_ERROR` |
| `error-envelope-not-found-with-hint.json` | C3 C4 | 失败信封走 `result`（非 error 帧），含 `hint` |
| `error-envelope-response-too-large.json` | C3 | 超大结果替换为 `RESPONSE_TOO_LARGE` 失败信封（截图专用 hint） |
| `notify-extensions-changed.json` | C14 | `{notification:"extensions.changed", params:{commands,removed}}` |
| `notify-game-stopped.json` | C5 | `{notification:"game_stopped"}`（无 params） |
