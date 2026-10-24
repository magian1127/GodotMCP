import assert from 'node:assert/strict'
import test from 'node:test'
import {
  buildMountConfig,
  buildSpawnPlan,
  errorName,
  isScriptEntry,
  mountConfigKey,
  PROJECT_PATH_ENV,
  DEFAULT_TOOL_CALL_TIMEOUT_MS,
} from '../mcp-mount.js'

test('isScriptEntry / buildSpawnPlan:shim 可执行文件直启,脚本入口经 node', () => {
  assert.equal(isScriptEntry('D:/gmcp/godot-mcp-shim.exe'), false)
  assert.equal(isScriptEntry('D:/gmcp/shim'), false)
  assert.equal(isScriptEntry('D:/gmcp/server/dist/index.js'), true)
  assert.equal(isScriptEntry('D:/gmcp/adapters/dsh/godot-http-bridge.mjs'), true)
  assert.equal(isScriptEntry('D:\\gmcp\\server\\index.cjs'), true)

  // 原生 shim:命令就是入口本身,无参数。
  assert.deepEqual(buildSpawnPlan('D:/gmcp/godot-mcp-shim.exe'), {
    command: 'D:/gmcp/godot-mcp-shim.exe',
    args: [],
  })
  // JS 入口:经 node 执行;node 路径可注入以便测试。
  assert.deepEqual(buildSpawnPlan('D:/gmcp/server/dist/index.js', 'D:/node/node.exe'), {
    command: 'D:/node/node.exe',
    args: ['D:/gmcp/server/dist/index.js'],
  })
})

test('buildMountConfig:stdio + 项目路径 env + cwd;失败关闭', () => {
  const config = buildMountConfig({
    serverName: 'godot',
    dist: 'D:/gmcp/godot-mcp-shim.exe',
    projectPath: 'D:/game',
    cwd: 'D:/game',
  })
  assert.deepEqual(config, {
    transport: 'stdio',
    serverName: 'godot',
    command: 'D:/gmcp/godot-mcp-shim.exe',
    args: [],
    env: { [PROJECT_PATH_ENV]: 'D:/game' },
    cwd: 'D:/game',
    toolCallTimeoutMs: DEFAULT_TOOL_CALL_TIMEOUT_MS,
    failOnStartupError: false,
  })
  // 失败关闭:编辑器未开/daemon 没起都不该让会话创建失败。
  assert.equal(config.failOnStartupError, false)
})

test('buildMountConfig:项目路径为空 → 不设 env(shim 按 cwd 兜底);cwd 为空 → 不覆盖', () => {
  const config = buildMountConfig({ serverName: 'godot', dist: 'D:/gmcp/shim.exe', projectPath: '  ', cwd: '' })
  assert.deepEqual(config.env, {})
  assert.equal('cwd' in config, false)
  assert.equal(config.command, 'D:/gmcp/shim.exe')
})

test('buildMountConfig:脚本入口经注入的 node 执行;超时可覆盖', () => {
  const config = buildMountConfig({
    serverName: 'gd47',
    dist: 'D:/gmcp/adapters/dsh/godot-http-bridge.mjs',
    nodeExecPath: 'D:/node/node.exe',
    toolCallTimeoutMs: 1000,
  })
  assert.equal(config.command, 'D:/node/node.exe')
  assert.deepEqual(config.args, ['D:/gmcp/adapters/dsh/godot-http-bridge.mjs'])
  assert.equal(config.toolCallTimeoutMs, 1000)
})

test('buildMountConfig:非法 serverName / 空 dist 直接抛错(不产生配置)', () => {
  assert.throws(() => buildMountConfig({ serverName: 'bad name!', dist: 'D:/a.exe' }), /serverName/)
  assert.throws(() => buildMountConfig({ serverName: '', dist: 'D:/a.exe' }), /serverName/)
  assert.throws(() => buildMountConfig({ serverName: 'godot', dist: '   ' }), /server dist/)
})

test('mountConfigKey:同配置稳定,路径/入口/cwd 变化即不同', () => {
  const base = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/game', cwd: 'D:/game' })
  const same = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/game', cwd: 'D:/game' })
  assert.equal(mountConfigKey(base), mountConfigKey(same))

  const otherProject = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/other', cwd: 'D:/game' })
  assert.notEqual(mountConfigKey(base), mountConfigKey(otherProject))

  const otherCwd = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/game', cwd: 'D:/other' })
  assert.notEqual(mountConfigKey(base), mountConfigKey(otherCwd))

  const otherDist = buildMountConfig({ serverName: 'godot', dist: 'D:/b.exe', projectPath: 'D:/game', cwd: 'D:/game' })
  assert.notEqual(mountConfigKey(base), mountConfigKey(otherDist))
})

test('errorName:只取类型名,不回显 message', () => {
  assert.equal(errorName(new TypeError('secret D:/path')), 'TypeError')
  assert.equal(errorName({ name: 'ValidationError', message: 'x' }), 'ValidationError')
  assert.equal(errorName('boom'), 'Error')
  assert.equal(errorName(undefined), 'Error')
})
