// daemon 端点准备:端口/状态目录解析、token 读取、可执行文件推导、
// 会话键派生、cwd 头编码、2026 面探测与自举流程。
//
// 全部用注入依赖,不碰真实 daemon、不读真实用户目录、不启动进程。
import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  DEFAULT_DAEMON_PORT,
  DAEMON_STATE_DIR_NAME,
  daemonExecutableName,
  daemonExeFromEnv,
  daemonExeFromShim,
  deriveSessionKey,
  encodeCwdHeader,
  normalizeCwd,
  prepareDaemonEndpoint,
  probeMcp2026,
  readDaemonToken,
  resolveDaemonPort,
  resolveDaemonStateDir,
} from '../daemon-endpoint.js'

test('resolveDaemonPort:默认 6590,env 覆盖,非法回退默认', () => {
  assert.equal(resolveDaemonPort({}), DEFAULT_DAEMON_PORT)
  assert.equal(resolveDaemonPort({ GODOT_MCP_DAEMON_PORT: '7001' }), 7001)
  assert.equal(resolveDaemonPort({ GODOT_MCP_DAEMON_PORT: '0' }), DEFAULT_DAEMON_PORT)
  assert.equal(resolveDaemonPort({ GODOT_MCP_DAEMON_PORT: 'abc' }), DEFAULT_DAEMON_PORT)
  assert.equal(resolveDaemonPort({ GODOT_MCP_DAEMON_PORT: '70000' }), DEFAULT_DAEMON_PORT)
})

test('resolveDaemonStateDir:平台默认 + 环境变量覆盖', () => {
  const home = 'D:/home/user'
  // Windows:%APPDATA% 优先,缺失时回退 home/AppData/Roaming。
  assert.equal(
    resolveDaemonStateDir({ env: { APPDATA: 'C:/Users/u/AppData/Roaming' }, platform: 'win32', home }),
    join('C:/Users/u/AppData/Roaming', DAEMON_STATE_DIR_NAME),
  )
  assert.equal(
    resolveDaemonStateDir({ env: {}, platform: 'win32', home }),
    join(home, 'AppData', 'Roaming', DAEMON_STATE_DIR_NAME),
  )
  assert.equal(
    resolveDaemonStateDir({ env: {}, platform: 'darwin', home }),
    join(home, 'Library', 'Application Support', DAEMON_STATE_DIR_NAME),
  )
  assert.equal(
    resolveDaemonStateDir({ env: { XDG_DATA_HOME: '/xdg' }, platform: 'linux', home }),
    join('/xdg', DAEMON_STATE_DIR_NAME),
  )
  assert.equal(
    resolveDaemonStateDir({ env: {}, platform: 'linux', home }),
    join(home, '.local', 'share', DAEMON_STATE_DIR_NAME),
  )
  // 显式覆盖优先于平台默认(上游同名变量)。
  assert.equal(
    resolveDaemonStateDir({ env: { GODOT_MCP_DAEMON_STATE_DIR: 'C:/state' }, platform: 'win32', home }),
    'C:/state',
  )
})

test('readDaemonToken:读出去空白;缺失/空文件返回 undefined(不抛)', () => {
  const dir = mkdtempSync(join(tmpdir(), 'godot-token-'))
  assert.equal(readDaemonToken(dir), undefined)
  writeFileSync(join(dir, 'daemon-token'), '  abc123\n')
  assert.equal(readDaemonToken(dir), 'abc123')
  writeFileSync(join(dir, 'daemon-token'), '   \n')
  assert.equal(readDaemonToken(dir), undefined)
})

test('daemonExeFromShim:同目录替换;脚本入口/空串推导不出', () => {
  assert.equal(daemonExecutableName('win32'), 'godot-mcp-daemon.exe')
  assert.equal(daemonExecutableName('linux'), 'godot-mcp-daemon')
  assert.equal(
    daemonExeFromShim('D:/gmcp/publish/win-x64/godot-mcp-shim.exe', 'win32'),
    join('D:/gmcp/publish/win-x64', 'godot-mcp-daemon.exe'),
  )
  assert.equal(daemonExeFromShim('D:/gmcp/shim/godot-mcp-shim', 'linux'), join('D:/gmcp/shim', 'godot-mcp-daemon'))
  // 历史 Node 桥入口:同目录没有原生 daemon,不自举。
  assert.equal(daemonExeFromShim('D:/gmcp/bridge/godot-http-bridge.mjs'), undefined)
  assert.equal(daemonExeFromShim('   '), undefined)
})

test('daemonExeFromEnv:显式覆盖;.dll 开发形态不自举', () => {
  assert.equal(daemonExeFromEnv({}), undefined)
  assert.equal(daemonExeFromEnv({ GODOT_MCP_DAEMON_EXE: 'D:/gmcp/godot-mcp-daemon.exe' }), 'D:/gmcp/godot-mcp-daemon.exe')
  assert.equal(daemonExeFromEnv({ GODOT_MCP_DAEMON_EXE: 'D:/gmcp/godot-mcp-daemon.dll' }), undefined)
})

test('deriveSessionKey:UUID 形状、同种子稳定、异种子不同', () => {
  const key = deriveSessionKey('session:s1')
  assert.match(key, /^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  assert.equal(deriveSessionKey('session:s1'), key)
  assert.notEqual(deriveSessionKey('session:s2'), key)
  assert.notEqual(deriveSessionKey('project:D:/a'), deriveSessionKey('session:D:/a'))
})

test('encodeCwdHeader:与原样语义一致的 uri: 百分号编码', () => {
  assert.equal(encodeCwdHeader('D:/game'), 'uri:D%3A%2Fgame')
  // 反斜杠统一成正斜杠(与上游 shim 同口径)。
  assert.equal(encodeCwdHeader('D:\\game\\proj'), 'uri:D%3A%2Fgame%2Fproj')
  // 中文按 UTF-8 百分号编码。
  assert.equal(encodeCwdHeader('D:/游戏'), 'uri:D%3A%2F%E6%B8%B8%E6%88%8F')
  // .NET Uri.EscapeDataString 会编码 !'()*;encodeURIComponent 不会,这里必须补齐。
  assert.equal(encodeCwdHeader('D:/a!(b)*'), 'uri:D%3A%2Fa%21%28b%29%2A')
  // 空格。
  assert.equal(encodeCwdHeader('D:/my game'), 'uri:D%3A%2Fmy%20game')
})

test('normalizeCwd:大小写/斜杠/尾斜杠等价', () => {
  assert.equal(normalizeCwd('D:\\Game\\Proj\\'), 'd:/game/proj')
  assert.equal(normalizeCwd('d:/game/proj'), 'd:/game/proj')
})

test('probeMcp2026:supportedVersions 含 2026-07-28 才算可用', async () => {
  const sse = (payload: unknown): string => `event: message\ndata: ${JSON.stringify(payload)}\n\n`
  const okFetch = (async () => new Response(sse({ result: { supportedVersions: ['2026-07-28'] } }), { status: 200 })) as unknown as typeof fetch
  assert.equal(await probeMcp2026('http://127.0.0.1:6590/', 't', { timeoutMs: 100, fetchImpl: okFetch }), true)

  // 旧版 daemon:没有 2026 面 → 不可直连(必须回退 stdio)。
  const legacyFetch = (async () => new Response(sse({ result: { supportedVersions: ['2025-11-25'] } }), { status: 200 })) as unknown as typeof fetch
  assert.equal(await probeMcp2026('http://127.0.0.1:6590/', 't', { timeoutMs: 100, fetchImpl: legacyFetch }), false)

  const errorFetch = (async () => new Response('{}', { status: 401 })) as unknown as typeof fetch
  assert.equal(await probeMcp2026('http://127.0.0.1:6590/', 't', { timeoutMs: 100, fetchImpl: errorFetch }), false)

  const throwingFetch = (async () => { throw new Error('ECONNREFUSED') }) as unknown as typeof fetch
  assert.equal(await probeMcp2026('http://127.0.0.1:6590/', 't', { timeoutMs: 100, fetchImpl: throwingFetch }), false)
})

test('prepareDaemonEndpoint:端口已通 + token + 2026 面 → 返回端点', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'godot-endpoint-'))
  writeFileSync(join(dir, 'daemon-token'), 'tok-1\n')
  const endpoint = await prepareDaemonEndpoint({
    serverDist: 'D:/gmcp/godot-mcp-shim.exe',
    env: { GODOT_MCP_DAEMON_PORT: '6599' },
    stateDir: dir,
    deps: {
      isPortOpen: async () => true,
      probe: async () => true,
      sleep: async () => {},
    },
  })
  assert.deepEqual(endpoint, { baseUrl: 'http://127.0.0.1:6599/', token: 'tok-1' })
})

test('prepareDaemonEndpoint:token 缺失 → undefined(回退 stdio)', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'godot-endpoint-'))
  const endpoint = await prepareDaemonEndpoint({
    serverDist: 'D:/gmcp/godot-mcp-shim.exe',
    stateDir: dir,
    tokenTimeoutMs: 0,
    deps: { isPortOpen: async () => true, probe: async () => true, sleep: async () => {} },
  })
  assert.equal(endpoint, undefined)
})

test('prepareDaemonEndpoint:旧版 daemon(2026 探测失败)→ undefined', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'godot-endpoint-'))
  writeFileSync(join(dir, 'daemon-token'), 'tok')
  const endpoint = await prepareDaemonEndpoint({
    serverDist: 'D:/gmcp/godot-mcp-shim.exe',
    stateDir: dir,
    deps: { isPortOpen: async () => true, probe: async () => false, sleep: async () => {} },
  })
  assert.equal(endpoint, undefined)
})

test('prepareDaemonEndpoint:端口不通但有同目录 daemon → 拉起并等就绪', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'godot-endpoint-'))
  writeFileSync(join(dir, 'daemon-token'), 'tok')
  const exe = join(dir, 'godot-mcp-daemon.exe')
  writeFileSync(exe, 'stub')
  const spawned: string[] = []
  let probes = 0
  const endpoint = await prepareDaemonEndpoint({
    serverDist: join(dir, 'godot-mcp-shim.exe'),
    env: {},
    stateDir: dir,
    bootstrapTimeoutMs: 2000,
    deps: {
      // 第一次不通(拉起前),之后通(拉起后)。
      isPortOpen: async () => {
        probes += 1
        return probes > 1
      },
      spawnDaemon: (path) => { spawned.push(path) },
      probe: async () => true,
      sleep: async () => {},
    },
  })
  assert.equal(endpoint?.token, 'tok')
  assert.deepEqual(spawned, [exe])
})

test('prepareDaemonEndpoint:端口不通且无 daemon → undefined,不抛', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'godot-endpoint-'))
  const endpoint = await prepareDaemonEndpoint({
    // 脚本入口推导不出 daemon 可执行文件。
    serverDist: 'D:/gmcp/bridge/godot-http-bridge.mjs',
    env: {},
    stateDir: dir,
    deps: { isPortOpen: async () => false, probe: async () => true, sleep: async () => {} },
  })
  assert.equal(endpoint, undefined)
})
