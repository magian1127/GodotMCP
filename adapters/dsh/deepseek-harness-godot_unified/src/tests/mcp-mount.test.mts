import assert from 'node:assert/strict'
import test from 'node:test'
import {
  buildHttpMountConfig,
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

test('buildMountConfig:stdio + cwd,不再注入 GODOT_MCP_PROJECT_PATH;失败关闭', () => {
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
    env: {},
    cwd: 'D:/game',
    toolCallTimeoutMs: DEFAULT_TOOL_CALL_TIMEOUT_MS,
    failOnStartupError: false,
  })
  // 失败关闭:编辑器未开/daemon 没起都不该让会话创建失败。
  assert.equal(config.failOnStartupError, false)
})

test('buildMountConfig:env 恒为空(目标由 daemon 解析);cwd 为空 → 不覆盖', () => {
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

test('mountConfigKey:同配置稳定;入口/cwd 变化即不同;项目路径不再影响它', () => {
  const base = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/game', cwd: 'D:/game' })
  const same = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/game', cwd: 'D:/game' })
  assert.equal(mountConfigKey(base), mountConfigKey(same))

  // **反向断言**:projectPath 已不参与 config(不再注入 env),所以它不该改变挂载身份键。
  // 若这里变红,说明有人把 GODOT_MCP_PROJECT_PATH 又加回去了。
  const otherProject = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/other', cwd: 'D:/game' })
  assert.equal(mountConfigKey(base), mountConfigKey(otherProject))

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

// ---------------------------------------------------------------------------
// streamable-http 分支:直连 daemon,每会话零子进程;工具面靠 x-godot-session 隔离。
// ---------------------------------------------------------------------------

test('buildHttpMountConfig:直连 Config 形状(会话键 + cwd 头 + 失败关闭)', () => {
  const config = buildHttpMountConfig({
    serverName: 'godot',
    baseUrl: 'http://127.0.0.1:6590/',
    token: 'tok-abc',
    sessionKey: 'aaaaaaaa-bbbb-5ccc-8ddd-eeeeeeeeeeee',
    cwd: 'D:/game',
  })
  assert.deepEqual(config, {
    transport: 'streamable-http',
    serverName: 'godot',
    url: 'http://127.0.0.1:6590/',
    headers: {
      authorization: 'Bearer tok-abc',
      'x-godot-session': 'aaaaaaaa-bbbb-5ccc-8ddd-eeeeeeeeeeee',
      'x-godot-shim-version': 'dsh-godot',
      'x-godot-cwd': 'uri:D%3A%2Fgame',
    },
    toolCallTimeoutMs: DEFAULT_TOOL_CALL_TIMEOUT_MS,
    failOnStartupError: false,
  })
})

test('buildHttpMountConfig:cwd 空则不设 x-godot-cwd;超时可覆盖', () => {
  const config = buildHttpMountConfig({
    serverName: 'godot',
    baseUrl: 'http://127.0.0.1:6590/',
    token: 'tok',
    sessionKey: 'k',
    cwd: '   ',
    toolCallTimeoutMs: 1000,
  })
  assert.equal('x-godot-cwd' in config.headers, false)
  assert.equal(config.toolCallTimeoutMs, 1000)
})

test('buildHttpMountConfig:非法 serverName / 空端点 / 空 token / 空会话键直接抛错', () => {
  const base = { serverName: 'godot', baseUrl: 'http://127.0.0.1:6590/', token: 't', sessionKey: 'k' }
  assert.throws(() => buildHttpMountConfig({ ...base, serverName: 'bad name!' }), /serverName/)
  assert.throws(() => buildHttpMountConfig({ ...base, baseUrl: '  ' }), /端点/)
  assert.throws(() => buildHttpMountConfig({ ...base, token: '  ' }), /token/)
  assert.throws(() => buildHttpMountConfig({ ...base, sessionKey: ' ' }), /会话键/)
})

test('mountConfigKey:stdio 与 http 前缀不同;端点/token/会话键变化即不同', () => {
  const stdio = buildMountConfig({ serverName: 'godot', dist: 'D:/a.exe', projectPath: 'D:/game', cwd: 'D:/game' })
  const http = buildHttpMountConfig({
    serverName: 'godot', baseUrl: 'http://127.0.0.1:6590/', token: 't', sessionKey: 'k', cwd: 'D:/game',
  })
  assert.notEqual(mountConfigKey(stdio), mountConfigKey(http))

  const again = buildHttpMountConfig({
    serverName: 'godot', baseUrl: 'http://127.0.0.1:6590/', token: 't', sessionKey: 'k', cwd: 'D:/game',
  })
  assert.equal(mountConfigKey(http), mountConfigKey(again))

  // token 轮换必须触发重挂(它写在已挂载连接的 headers 里)。
  const rotated = buildHttpMountConfig({
    serverName: 'godot', baseUrl: 'http://127.0.0.1:6590/', token: 't2', sessionKey: 'k', cwd: 'D:/game',
  })
  assert.notEqual(mountConfigKey(http), mountConfigKey(rotated))

  // 会话键作用域变化(session ↔ project)同样重挂。
  const otherKey = buildHttpMountConfig({
    serverName: 'godot', baseUrl: 'http://127.0.0.1:6590/', token: 't', sessionKey: 'k2', cwd: 'D:/game',
  })
  assert.notEqual(mountConfigKey(http), mountConfigKey(otherKey))
})
