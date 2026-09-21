// resolve/validate 纯函数测试。
import { mkdtempSync, rmSync, mkdirSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { resolveServerDist, toForwardSlashes, validateGodotProject, validateServerDistFile } from '../bin/resolve.mjs'

test('toForwardSlashes:反斜杠全转正斜杠', () => {
  assert.equal(toForwardSlashes('D:\\a\\b\\c.js'), 'D:/a/b/c.js')
  assert.equal(toForwardSlashes('D:/a/b'), 'D:/a/b')
})

test('resolveServerDist 优先级:flag > env > root 推导(bridge 候选优先);全缺失时给可行动错误', () => {
  const flag = resolveServerDist({ serverDist: 'D:/x/dist/index.js', env: { GODOT_MCP_SERVER_DIST: 'D:/y/dist/index.js' } })
  assert.equal(flag.ok, true)
  assert.equal(flag.path, 'D:/x/dist/index.js')

  const envOnly = resolveServerDist({ env: { GODOT_MCP_SERVER_DIST: 'D:/y/dist/index.js' } })
  assert.equal(envOnly.ok, true)
  assert.equal(envOnly.path, 'D:/y/dist/index.js')

  const rootBridge = resolveServerDist({ godotMcpRoot: 'D:\\ws\\GodotMCP', env: {}, fileExists: () => true })
  assert.equal(rootBridge.ok, true)
  assert.equal(rootBridge.path, 'D:/ws/GodotMCP/adapters/dsh/godot-http-bridge.mjs')

  const rootLegacy = resolveServerDist({ godotMcpRoot: 'D:\\ws\\GodotMCP', env: {}, fileExists: p => p.endsWith('index.js') })
  assert.equal(rootLegacy.ok, true)
  assert.equal(rootLegacy.path, 'D:/ws/GodotMCP/plugin/godot-mcp-unified/server/dist/index.js')

  const rootMissing = resolveServerDist({ godotMcpRoot: 'D:\\ws\\GodotMCP', env: {}, fileExists: () => false })
  assert.equal(rootMissing.ok, true)
  assert.equal(rootMissing.path, 'D:/ws/GodotMCP/adapters/dsh/godot-http-bridge.mjs')

  const envRoot = resolveServerDist({ env: { GODOT_MCP_ROOT: 'D:/ws/GodotMCP' }, fileExists: () => true })
  assert.equal(envRoot.ok, true)
  assert.equal(envRoot.path, 'D:/ws/GodotMCP/adapters/dsh/godot-http-bridge.mjs')

  const none = resolveServerDist({ env: {} })
  assert.equal(none.ok, false)
  assert.ok(none.reason.includes('--server-dist'))
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
