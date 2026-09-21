import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createServer } from 'node:net'
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { editorStatus, normalizeProjectKey, readRegistryEntries, registryFilePath, serverDistStatus } from '../workbench/probes.js'

test('registryFilePath:win 下位于 APPDATA', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wb-probe-'))
  const prev = process.env.APPDATA
  process.env.APPDATA = dir
  try {
    assert.ok(registryFilePath().startsWith(dir.replace(/\\/g, '/').slice(0, 2)) || registryFilePath().includes('godot-mcp-toolkit'))
    // 无条目 → null
    assert.equal(await editorStatus('D:/games/x'), null)
  } finally { process.env.APPDATA = prev; rmSync(dir, { recursive: true, force: true }) }
})

test('normalizeProjectKey:反斜杠/尾斜杠/小写', () => {
  assert.equal(normalizeProjectKey('D:\\Games\\X/'), 'd:/games/x')
})

test('readRegistryEntries + editorStatus:有条目时报告端口与监听', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wb-probe-'))
  const prev = process.env.APPDATA
  process.env.APPDATA = dir
  const server = createServer()
  await new Promise<void>(r => server.listen(0, '127.0.0.1', r))
  const port = (server.address() as { port: number }).port
  mkdirSync(join(dir, 'godot-mcp-toolkit'), { recursive: true })
  writeFileSync(join(dir, 'godot-mcp-toolkit', 'projects.json'), JSON.stringify({ by_path: { 'd:/games/x': { port, pid: process.pid, godot_version: '4.7' } } }))
  try {
    process.env.APPDATA = undefined
    process.env.APPDATA = dir
    const entries = readRegistryEntries()
    assert.ok(entries.has('d:/games/x'))
    const status = await editorStatus('D:/games/x')
    assert.equal(status!.port, port)
    assert.equal(status!.listening, true)
    assert.equal(status!.alive, true)
    assert.equal(status!.godotVersion, '4.7')
  } finally {
    process.env.APPDATA = prev
    await new Promise<void>(r => server.close(() => r()))
    rmSync(dir, { recursive: true, force: true })
  }
})

test('readRegistryEntries:兼容新 entries/*.json 布局(每条含 _key)', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wb-probe-ent-'))
  const prev = process.env.APPDATA
  process.env.APPDATA = dir
  const server = createServer()
  await new Promise<void>(r => server.listen(0, '127.0.0.1', r))
  const port = (server.address() as { port: number }).port
  const toolkit = join(dir, 'godot-mcp-toolkit')
  mkdirSync(join(toolkit, 'entries'), { recursive: true })
  // 上游新布局:仅 entries/<hash>.json,条目带 _key(小写规范路径 1 份)。
  writeFileSync(join(toolkit, 'entries', 'abc.json'), JSON.stringify({ _key: 'd:/games/newproj', port, pid: process.pid, godot_version: '4.7' }))
  // 旧 projects.json 仍在但过时;新条目同键覆盖旧值。
  writeFileSync(join(toolkit, 'projects.json'), JSON.stringify({ by_path: { 'd:/games/newproj': { port: 1, pid: 0, godot_version: 'old' } } }))
  try {
    process.env.APPDATA = dir
    const entries = readRegistryEntries()
    assert.ok(entries.has('d:/games/newproj'))
    const status = await editorStatus('D:/Games/NewProj')
    assert.equal(status!.port, port)          // 新 entries 覆盖旧 by_path 同键
    assert.equal(status!.godotVersion, '4.7')
    assert.equal(status!.listening, true)
    assert.equal(status!.alive, true)
    assert.equal(status!.project, 'D:/Games/NewProj')
  } finally {
    process.env.APPDATA = prev
    await new Promise<void>(r => server.close(() => r()))
    rmSync(dir, { recursive: true, force: true })
  }
})

test('readRegistryEntries:跳过 <hash>.runtime.json(游戏启动时不覆盖编辑器条目)', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wb-probe-rt-'))
  const prev = process.env.APPDATA
  process.env.APPDATA = dir
  const server = createServer()
  await new Promise<void>(r => server.listen(0, '127.0.0.1', r))
  const port = (server.address() as { port: number }).port
  const toolkit = join(dir, 'godot-mcp-toolkit')
  mkdirSync(join(toolkit, 'entries'), { recursive: true })
  // 编辑器条目:<hash>.json,正常 port。
  writeFileSync(join(toolkit, 'entries', 'hash1.json'), JSON.stringify({
    _key: 'd:/games/live', port, pid: process.pid, godot_version: '4.7',
    runtime_port: null, runtime_pid: null,
  }))
  // 运行时条目:<hash>.runtime.json —— 游戏从编辑器启动时由运行时自动加载写入,
  // port 恒为 -1、_key 与编辑器条目相同(见上游 registry_client.gd set_runtime)。
  writeFileSync(join(toolkit, 'entries', 'hash1.runtime.json'), JSON.stringify({
    _key: 'd:/games/live', port: -1, token_path: '', pid: 424242,
    godot_version: '4.7', runtime_port: 6570, runtime_pid: 424242,
    lsp_host: '127.0.0.1', lsp_port: null,
  }))
  try {
    process.env.APPDATA = dir
    const entries = readRegistryEntries()
    const entry = entries.get('d:/games/live')
    // 编辑器条目不被运行时条目覆盖:port 保持编辑器值。
    assert.equal(entry?.port, port)
    // editorStatus 全链路不抛(修复前:portListening(-1) 同步抛 ERR_SOCKET_BAD_PORT)。
    const status = await editorStatus('D:/games/live')
    assert.equal(status!.port, port)
    assert.equal(status!.listening, true)
    assert.equal(status!.alive, true)
  } finally {
    process.env.APPDATA = prev
    await new Promise<void>(r => server.close(() => r()))
    rmSync(dir, { recursive: true, force: true })
  }
})

test('readRegistryEntries:port -1 的残留条目不致 status 500(端口防御)', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'wb-probe-neg-'))
  const prev = process.env.APPDATA
  process.env.APPDATA = dir
  const toolkit = join(dir, 'godot-mcp-toolkit')
  mkdirSync(toolkit, { recursive: true })
  // 模拟仅运行时条目在场(编辑器已关、runtime 文件残留)或损坏注册表:
  // 旧 projects.json 读模型里 port 可能为 -1。
  writeFileSync(join(toolkit, 'projects.json'), JSON.stringify({ by_path: { 'd:/games/onlyrt': { port: -1, pid: 1, godot_version: '4.7' } } }))
  try {
    process.env.APPDATA = dir
    const entries = readRegistryEntries()
    assert.equal(entries.get('d:/games/onlyrt')?.port, -1)
    // portListening 对 -1 返回 false 而不是同步抛 ERR_SOCKET_BAD_PORT。
    const status = await editorStatus('d:/games/onlyrt')
    assert.equal(status!.port, -1)
    assert.equal(status!.listening, false)
  } finally {
    process.env.APPDATA = prev
    rmSync(dir, { recursive: true, force: true })
  }
})

test('serverDistStatus:存在性与正斜杠路径', () => {
  const dir = mkdtempSync(join(tmpdir(), 'wb-dist-'))
  try {
    const rel = 'plugin/godot-mcp-unified/server/dist/index.js'
    const miss = serverDistStatus(dir, rel)
    assert.equal(miss.exists, false)
    mkdirSync(join(dir, ...rel.split('/').slice(0, -1)), { recursive: true })
    writeFileSync(join(dir, ...rel.split('/')), '#!/usr/bin/env node\n')
    const hit = serverDistStatus(dir, rel)
    assert.equal(hit.exists, true)
    assert.ok(!hit.path!.includes('\\'))
  } finally { rmSync(dir, { recursive: true, force: true }) }
})
