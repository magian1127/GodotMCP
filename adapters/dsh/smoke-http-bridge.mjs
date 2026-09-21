// DSH 翻转冒烟(issue 18):以 DSH 工作台的既有拉起形态驱动 godot-http-bridge.mjs。
// 用法:node adapters/dsh/smoke-http-bridge.mjs  (需 daemon 在 6590 监听)
import { spawn } from 'node:child_process'

const bridgePath = new URL('./godot-http-bridge.mjs', import.meta.url).pathname
  .replace(/^\/([A-Za-z]:)/, '$1')
const proc = spawn(process.execPath, [bridgePath], { stdio: ['pipe', 'pipe', 'pipe'] })

let buffer = ''
let exitCode = 0
const pending = new Map()

function send(obj) {
  proc.stdin.write(JSON.stringify(obj) + '\n')
}

function request(id, method, params, timeoutMs = 30_000) {
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    send({ jsonrpc: '2.0', id, method, params })
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id)
        reject(new Error(`timeout waiting id=${id} (${method})`))
      }
    }, timeoutMs)
  })
}

proc.stdout.setEncoding('utf8')
proc.stdout.on('data', (chunk) => {
  buffer += chunk
  for (;;) {
    const index = buffer.indexOf('\n')
    if (index === -1) break
    const line = buffer.slice(0, index).trim()
    buffer = buffer.slice(index + 1)
    if (line === '') continue
    const message = JSON.parse(line)
    if (message.id !== undefined && pending.has(message.id)) {
      const entry = pending.get(message.id)
      pending.delete(message.id)
      entry.resolve(message)
    } else if (message.method === 'notifications/tools/list_changed') {
      console.log('list_changed received ✓')
    }
  }
})
proc.stderr.setEncoding('utf8')
proc.stderr.on('data', (chunk) => process.stderr.write(`[bridge] ${chunk}`))

// 请求 id 刻意避开 1/2/3 这类小整数:桥内部也用自增 id 转发给 daemon,
// 若桥把 daemon 侧的 id 原样写回(而非回写 host 的 id),小 id 会偶然"
// 撞对"而让缺陷静默通过 —— 真实 host(DSH/Codex)用的是自己的 id 序列,
// 撞不上就表现为 initialize 超时。用高位 id 让该缺陷必然暴露。
const init = await request(1001, 'initialize', {
  protocolVersion: '2024-11-05',
  capabilities: {},
  clientInfo: { name: 'dsh-smoke', version: '1' },
})
console.log('initialize ✓ server =', init.result.serverInfo.name)
send({ jsonrpc: '2.0', method: 'notifications/initialized' })

const tools = await request(1002, 'tools/list', {})
console.log('tools/list ✓ count =', tools.result.tools.length)

const call = await request(1003, 'tools/call', { name: 'list_instances', arguments: {} })
console.log('tools/call list_instances ✓ isError =', call.result.isError ?? false)

// list_changed:延迟 1.5s 后从另一连接激活一个组(触发 daemon 扇出)。
setTimeout(async () => {
  const token = (await import('node:fs')).readFileSync(
    process.env.APPDATA + '\\godot-mcp-toolkit\\daemon-token', 'utf8').trim()
  const response = await fetch('http://127.0.0.1:6590/', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json, text/event-stream',
      Authorization: `Bearer ${token}`,
      'MCP-Protocol-Version': '2025-11-25',
    },
    body: JSON.stringify({ jsonrpc: '2.0', id: 99, method: 'tools/call', params: {
      name: 'discover_tools', arguments: { request: ['audio'] } } }),
  })
  await response.text()
  console.log('discover_tools(audio re-activate) triggered ✓')
}, 1500)

// 等待可能的 list_changed 到达(经 listen 长流),随后收尾。
await new Promise((resolve) => setTimeout(resolve, 4000))
proc.stdin.end()
proc.on('exit', (code) => {
  if (exitCode === 0 && code !== 0) exitCode = code
  console.log('smoke done, bridge exit =', code)
  process.exit(exitCode)
})
setTimeout(() => {
  console.log('smoke done (bridge still running — expected for stdio server)')
  proc.kill()
  process.exit(exitCode)
}, 1500)
