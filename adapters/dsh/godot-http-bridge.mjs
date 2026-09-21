// godot-http-bridge.mjs —— DSH 翻转自举桥(issue 18)。
//
// DSH 工作台以 `node <godotServerDist>` 拉起 Godot 控制面(stdio,行帧 JSON-RPC)。
// daemon 翻转后,把 DSH 的 godotServerDist 指向本文件:本桥把 stdio 行帧透明转发到
// daemon 的 loopback HTTP 面(initialize/tools/list/tools/call),并把
// subscriptions/listen 长流上的 notifications/tools/list_changed 以同名通知回写
// stdout —— DSH 侧零改动完成翻转(01 号结论:DSH 这类 stdio-only host 走 shim;
// 本文件即 shim 的 JS 形态,复用 DSH 已有的 node 运行时,免装 .NET 运行时)。
//
// 环境变量:
//   GODOT_MCP_DAEMON_URL   daemon HTTP 面(默认 http://127.0.0.1:6590/)
//   GODOT_MCP_DAEMON_TOKEN 覆盖 token(默认读机器级注册表目录的 daemon-token)
//
// 用法(即 DSH 的既有拉起形态):node godot-http-bridge.mjs

import { createInterface } from 'node:readline'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const DAEMON_URL = process.env.GODOT_MCP_DAEMON_URL ?? 'http://127.0.0.1:6590/'
const TOKEN = process.env.GODOT_MCP_DAEMON_TOKEN
  ?? (() => {
    try {
      return readFileSync(join(process.env.APPDATA ?? '', 'godot-mcp-toolkit', 'daemon-token'), 'utf8').trim()
    } catch {
      return ''
    }
  })()

const HEADERS = {
  'Content-Type': 'application/json',
  Accept: 'application/json, text/event-stream',
  Authorization: `Bearer ${TOKEN}`,
}

// 协商后的协议版本:initialize 成功前不带 MCP-Protocol-Version 头
// (握手期头体必须一致;版本头错配会被 daemon 依 SEP-2575 以 -32020 拒收)。
let negotiated = undefined

let nextId = 1
let initialized = false

function writeOut(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n')
}

function writeError(id, code, message) {
  if (id === undefined || id === null) return
  writeOut({ jsonrpc: '2.0', id, error: { code, message } })
}

/** POST 一条 JSON-RPC 到 daemon,解析 SSE/JSON 负载并取回应答。
 *
 * hostId:host 侧原始请求 id。写回给 host 时必须沿用它 ——
 * 桥自增的 nextId 只用于 daemon 侧去重,绝不能出现在写回帧里
 * (否则 host 按自己的 id 匹配不到应答,表现为 initialize 超时)。
 * 省略 hostId 时退回桥的 wireId(内部调用场景)。 */
async function postRpc(method, params, hostId) {
  const wireId = nextId++
  const response = await fetch(DAEMON_URL, {
    method: 'POST',
    headers: { ...HEADERS, ...(negotiated !== undefined ? { 'MCP-Protocol-Version': negotiated } : {}) },
    body: JSON.stringify({ jsonrpc: '2.0', id: wireId, method, params }),
  })
  const text = await response.text()
  if (!response.ok) {
    throw new Error(`daemon HTTP ${response.status}: ${text.slice(0, 200)}`)
  }
  const dataLine = text.split('\n').find((l) => l.startsWith('data:'))
  const json = dataLine ? dataLine.slice(5).trim() : text
  const reply = JSON.parse(json)
  if (hostId !== undefined && reply !== null && typeof reply === 'object') {
    reply.id = hostId
  }
  return reply
}

/** subscriptions/listen 长流:回写 tools/list_changed(与旧 server 行为一致)。 */
async function startListen() {
  try {
    // listen 是 2026-07-28 专用信封:头与 _meta 都必须携带该版本(与协商版本无关),
    // 且标准头 Mcp-Method 强制。
    const response = await fetch(DAEMON_URL, {
      method: 'POST',
      headers: {
        ...HEADERS,
        'MCP-Protocol-Version': '2026-07-28',
        'Mcp-Method': 'subscriptions/listen',
      },
      body: JSON.stringify({
        jsonrpc: '2.0',
        id: 'listen',
        method: 'subscriptions/listen',
        params: {
          notifications: { toolsListChanged: true },
          _meta: {
            'io.modelcontextprotocol/protocolVersion': '2026-07-28',
            'io.modelcontextprotocol/clientCapabilities': {},
            'io.modelcontextprotocol/clientInfo': { name: 'dsh-http-bridge', version: '1.0.0' },
          },
        },
      }),
    })
    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    for (;;) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      let index
      while ((index = buffer.indexOf('\n')) !== -1) {
        const line = buffer.slice(0, index).trim()
        buffer = buffer.slice(index + 1)
        if (!line.startsWith('data:')) continue
        try {
          const message = JSON.parse(line.slice(5).trim())
          if (message.method === 'notifications/tools/list_changed') {
            writeOut({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' })
          }
        } catch {
          // 非 JSON 行(SSE 前缀等)忽略。
        }
      }
    }
  } catch {
    // 长流中断:daemon/网络问题,静默收尾(调用路径的错误已足够定位)。
  }
}

const rl = createInterface({ input: process.stdin })
rl.on('line', (line) => {
  const trimmed = line.trim()
  if (trimmed === '') return
  let request
  try {
    request = JSON.parse(trimmed)
  } catch {
    return
  }
  const { id, method, params } = request
  if (method === 'initialize') {
    initialized = true
    void postRpc('initialize', params, id)
      .then((reply) => {
        // 握手成功:锁定协商版本,后续请求带同版本头。
        if (reply.result?.protocolVersion !== undefined) {
          negotiated = reply.result.protocolVersion
        }
        writeOut(reply)
      })
      .catch((error) => writeError(id, -32603, String(error)))
    void startListen()
    return
  }
  if (method === 'notifications/initialized') {
    return
  }
  if (!initialized) {
    writeError(id, -32000, 'bridge not initialized')
    return
  }
  if (method === 'tools/list' || method === 'tools/call') {
    void postRpc(method, params ?? {}, id)
      .then((reply) => writeOut(reply))
      .catch((error) => writeError(id, -32603, String(error)))
    return
  }
  // 其余方法(daemon 只服务 tools/* 与状态面)按 unsupported 回应。
  writeError(id, -32601, `Method not found: ${method}`)
})

process.stdin.on('end', () => {
  process.exit(0)
})
