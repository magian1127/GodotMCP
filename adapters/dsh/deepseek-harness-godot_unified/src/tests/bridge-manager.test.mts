// 桥接管理器:正常 JSON-RPC 握手/清单往返 + stdout 单行超限的协议违规终止。
// 用临时 .js 文件充当 GodotMCP server(spawn 真实 node 子进程,行为与线上一致)。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { GodotBridge, MAX_STDOUT_LINE_BYTES } from '../bridge/manager.js'

function scriptDir(): string {
  return mkdtempSync(join(tmpdir(), `godot-bridge-${Date.now()}-`))
}

/** 正常 server:按行回 JSON-RPC 响应(initialize → 空结果;tools/list → 两个工具)。 */
function echoServerFile(): string {
  const dir = scriptDir()
  const file = join(dir, 'echo.js')
  writeFileSync(file, [
    'let buf = ""',
    'process.stdin.setEncoding("utf8")',
    'process.stdin.on("data", chunk => {',
    '  buf += chunk',
    '  for (;;) {',
    '    const i = buf.indexOf("\\n")',
    '    if (i === -1) break',
    '    const line = buf.slice(0, i).trim()',
    '    buf = buf.slice(i + 1)',
    '    if (line === "") continue',
    '    try {',
    '      const m = JSON.parse(line)',
    '      if (m.id === undefined) continue',
    '      const result = m.method === "tools/list"',
    '        ? { tools: [{ name: "t1", description: "d1" }, { name: "t2" }] }',
    '        : {}',
    '      process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id: m.id, result }) + "\\n")',
    '    } catch {}',
    '  }',
    '})',
    'setInterval(() => {}, 60_000)',
  ].join('\n'), 'utf8')
  return file
}

/** 失控 server:单行输出超上限且无换行(协议违规 → 桥接必须终止而非吃满内存)。 */
function floodServerFile(): string {
  const dir = scriptDir()
  const file = join(dir, 'flood.js')
  writeFileSync(file, `process.stdout.write("x".repeat(${MAX_STDOUT_LINE_BYTES + 1024}))\nsetInterval(() => {}, 60_000)`, 'utf8')
  return file
}

function bridgeFor(dist: string): GodotBridge {
  return new GodotBridge({
    serverDist: () => dist,
    projectPath: () => 'C:/proj/godot',
    onListChanged: () => {},
    onExit: () => {},
    idleCloseMs: 60_000,
  })
}

test('正常路径:initialize 握手 + tools/list 精确清单往返', async () => {
  const bridge = bridgeFor(echoServerFile())
  try {
    const tools = await bridge.listTools()
    assert.deepEqual(tools.map(t => t.name), ['t1', 't2'])
    assert.equal(tools[0]!.description, 'd1')
    const outcome = await bridge.callTool('t1', { a: 1 }, undefined, 10_000)
    assert.deepEqual(outcome, {})
  } finally {
    bridge.stop()
  }
})

test('cwd 变化:已启动 server 用旧配置时按需重启', async () => {
  const bridge = bridgeFor(echoServerFile())
  try {
    bridge.setCwd('C:/a')
    await bridge.ensureStarted()
    bridge.setCwd('C:/b')
    await bridge.ensureStarted()
    // 旧进程的 exit 事件晚于新进程接替到达:不得误清新进程注册(代数保护),
    // 否则后续 stop() 杀不到新进程 → 桥接进程泄漏。
    await new Promise(resolve => setTimeout(resolve, 300))
    assert.equal(bridge.running, true, '旧进程退出不得注销新进程注册')
    const tools = await bridge.listTools()
    assert.equal(tools.length, 2)
  } finally {
    bridge.stop()
  }
})

test('stdout 单行超限:在途请求立即失败(协议违规),不等 30s initialize 超时', async () => {
  const bridge = bridgeFor(floodServerFile())
  const started = Date.now()
  try {
    await assert.rejects(bridge.listTools(), /stdout line exceeded/)
    assert.ok(Date.now() - started < 10_000, '应立即失败而非等到超时')
  } finally {
    bridge.stop()
  }
})
