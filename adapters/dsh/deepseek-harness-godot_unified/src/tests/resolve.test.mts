// resolve/validate 纯函数测试。
import { mkdtempSync, rmSync, mkdirSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { resolveServerDist, toForwardSlashes, validateGodotProject, validateServerDistFile } from '../bin/resolve.mjs'
import { shimEntryRelative } from '../bin/cli/constants.mjs'
import { buildSpawnPlan } from '../bridge/manager.js'

/** 随包 shim 入口在该 RID 下的期望路径(RID 随平台变化,故与实现共用同一构造函数)。 */
const shimExpected = (root: string): string => `${root}/${shimEntryRelative()}`

test('toForwardSlashes:反斜杠全转正斜杠', () => {
  assert.equal(toForwardSlashes('D:\\a\\b\\c.js'), 'D:/a/b/c.js')
  assert.equal(toForwardSlashes('D:/a/b'), 'D:/a/b')
})

test('resolveServerDist 优先级:flag > env > root 推导(随包 shim 候选优先);全缺失时给可行动错误', () => {
  const flag = resolveServerDist({ serverDist: 'D:/x/godot-mcp-shim.exe', env: { GODOT_MCP_SERVER_DIST: 'D:/y/godot-http-bridge.mjs' } })
  assert.equal(flag.ok, true)
  assert.equal(flag.path, 'D:/x/godot-mcp-shim.exe')

  const envOnly = resolveServerDist({ env: { GODOT_MCP_SERVER_DIST: 'D:/y/godot-http-bridge.mjs' } })
  assert.equal(envOnly.ok, true)
  assert.equal(envOnly.path, 'D:/y/godot-http-bridge.mjs')

  // 候选顺序:随包 shim(标准形态)→ stdio 兜底桥 → legacy Node 桥。
  const rootShim = resolveServerDist({ godotMcpRoot: 'D:\\ws\\GodotMCP', env: {}, fileExists: () => true })
  assert.equal(rootShim.ok, true)
  assert.equal(rootShim.path, shimExpected('D:/ws/GodotMCP'))

  const rootBridge = resolveServerDist({
    godotMcpRoot: 'D:\\ws\\GodotMCP',
    env: {},
    fileExists: p => p.endsWith('godot-http-bridge.mjs'),
  })
  assert.equal(rootBridge.ok, true)
  assert.equal(rootBridge.path, 'D:/ws/GodotMCP/adapters/dsh/godot-http-bridge.mjs')

  const rootLegacy = resolveServerDist({ godotMcpRoot: 'D:\\ws\\GodotMCP', env: {}, fileExists: p => p.endsWith('index.js') })
  assert.equal(rootLegacy.ok, true)
  assert.equal(rootLegacy.path, 'D:/ws/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js')

  // 都不存在时回退到随包 shim 候选(标准形态),让 validate 报出可行动路径。
  const rootMissing = resolveServerDist({ godotMcpRoot: 'D:\\ws\\GodotMCP', env: {}, fileExists: () => false })
  assert.equal(rootMissing.ok, true)
  assert.equal(rootMissing.path, shimExpected('D:/ws/GodotMCP'))

  const envRoot = resolveServerDist({ env: { GODOT_MCP_ROOT: 'D:/ws/GodotMCP' }, fileExists: () => true })
  assert.equal(envRoot.ok, true)
  assert.equal(envRoot.path, shimExpected('D:/ws/GodotMCP'))

  const none = resolveServerDist({ env: {} })
  assert.equal(none.ok, false)
  assert.ok(none.reason.includes('--server-dist'))
})

test('buildSpawnPlan:脚本经 node 执行,其余按可执行文件直接 spawn', () => {
  assert.deepEqual(buildSpawnPlan('D:/ws/GodotMCP/adapters/dsh/godot-http-bridge.mjs', 'NODE'), {
    command: 'NODE',
    args: ['D:/ws/GodotMCP/adapters/dsh/godot-http-bridge.mjs'],
  })
  assert.deepEqual(buildSpawnPlan('D:/ws/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js', 'NODE'), {
    command: 'NODE',
    args: ['D:/ws/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js'],
  })
  // 标准入口:随包 shim 是 native 可执行文件,直接 spawn、无 args。
  assert.deepEqual(buildSpawnPlan(`D:/ws/GodotMCP/${shimEntryRelative('win-x64')}`, 'NODE'), {
    command: `D:/ws/GodotMCP/${shimEntryRelative('win-x64')}`,
    args: [],
  })
  // 非 Windows 平台入口不带 .exe 后缀,同样按可执行文件直连。
  assert.deepEqual(buildSpawnPlan(`/ws/GodotMCP/${shimEntryRelative('linux-x64')}`, 'NODE'), {
    command: `/ws/GodotMCP/${shimEntryRelative('linux-x64')}`,
    args: [],
  })
})

test('validateServerDistFile:存在普通文件才通过', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dsh-godot-res-'))
  try {
    const file = join(dir, 'index.js')
    writeFileSync(file, '#!/usr/bin/env node\n')
    assert.equal(validateServerDistFile(file).ok, true)
    assert.equal(validateServerDistFile(join(dir, 'nope.js')).ok, false)
    assert.equal(validateServerDistFile(dir).ok, false) // 目录不是普通文件
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('validateGodotProject:绝对路径 + project.godot 缺一不可', () => {
  const dir = mkdtempSync(join(tmpdir(), 'dsh-godot-proj-'))
  try {
    const proj = join(dir, 'game')
    mkdirSync(proj)
    writeFileSync(join(proj, 'project.godot'), '; engine config\n')
    const ok = validateGodotProject(proj)
    assert.equal(ok.ok, true)
    assert.equal(ok.path, toForwardSlashes(proj))

    assert.equal(validateGodotProject(dir).ok, false) // 缺 project.godot
    assert.equal(validateGodotProject(join(dir, 'missing')).ok, false) // 不存在
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
