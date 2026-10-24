// 路径设置路由装配 + 路径存储:1 条 GET/POST 合并路由、同源/回环三重围栏、
// 方法/体积/JSON 校验、PathStore 归一化与持久化。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdirSync, rmSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { mountPathSettingsRoute, PATH_SETTINGS_ROUTE } from '../path-settings-route.js'
import { PathStore, cleanPath, normalizeProjects } from '../path-store.js'

interface Captured { path: string; handler: (req: unknown, res: unknown) => void }

function fakeCtx() {
  const captured: Captured[] = []
  const webServer = {
    register: (opt: { path: string; handler: (req: unknown, res: unknown) => void }) => {
      captured.push({ path: opt.path, handler: opt.handler })
      return () => { const i = captured.findIndex(c => c.path === opt.path && c.handler === opt.handler); if (i >= 0) captured.splice(i, 1) }
    },
  }
  return { captured, get: (name: string) => (name === 'webServer' ? webServer : undefined) }
}

function reqRes(method: string, options: {
  origin?: string
  body?: string
  host?: string
  remoteAddress?: string
  closeOnly?: boolean
  omitSocket?: boolean
  extraHeaders?: Record<string, string>
  url?: string
} = {}) {
  const chunks: Buffer[] = []
  const req: Record<string, unknown> = {
    method,
    url: options.url ?? PATH_SETTINGS_ROUTE,
    headers: {
      ...(options.origin !== undefined ? { origin: options.origin } : {}),
      host: options.host ?? '127.0.0.1:3080',
      ...(options.extraHeaders ?? {}),
    },
    on: (event: string, cb: (chunk?: Buffer) => void) => {
      if (event === 'data' && options.body !== undefined) cb(Buffer.from(options.body))
      if (event === 'end' && options.closeOnly !== true) cb()
      if (event === 'close' && options.closeOnly === true) cb()
    },
  }
  if (options.omitSocket !== true) req.socket = { remoteAddress: options.remoteAddress ?? '127.0.0.1' }
  const res = {
    statusCode: 0,
    body: '',
    setHeader: () => {},
    end: (payload?: string) => { res.body = payload ?? '' },
  }
  return { req, res }
}

const tick = async (): Promise<void> => { await new Promise<void>(r => setImmediate(r)) }

function tempStore(): PathStore {
  return new PathStore(join(tmpdir(), `godot-paths-${Date.now()}-${Math.random().toString(16).slice(2)}`, 'paths.json'))
}

test('装配:webServer 在场时挂 1 条路由(GET/POST 合并),disposer 可逆', () => {
  const ctx = fakeCtx()
  const dispose = mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  assert.notEqual(dispose, undefined)
  assert.equal(ctx.captured.length, 1)
  assert.equal(ctx.captured[0]!.path, '/godot-config/api/path-settings')
  dispose!()
  assert.equal(ctx.captured.length, 0)
})

test('装配:webServer 缺席时返回 undefined(不抛错)', () => {
  const ctx = { get: () => undefined }
  assert.equal(mountPathSettingsRoute(ctx as never, { pathStore: tempStore() }), undefined)
})

test('GET:读整份(全局 serverDist + 批量 projects),current* 随 cwd 解析', async () => {
  const store = tempStore()
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, {
    pathStore: store,
    serverDistPath: 'D:/global/dist/index.js',
    projectPath: 'D:/global/proj',
  })
  const handle = ctx.captured[0]!.handler
  // 无 cwd(配置表单场景):返回整份全局,current* 为 null。
  const noCwd = reqRes('GET')
  handle(noCwd.req, noCwd.res); await tick()
  const noCwdBody = JSON.parse(noCwd.res.body)
  assert.equal(noCwdBody.serverDist, 'D:/global/dist/index.js', '无 cwd 时 serverDist 回退全局')
  assert.deepEqual(noCwdBody.projects, [])
  assert.equal(noCwdBody.currentCwd, null)
  assert.equal(noCwdBody.currentProjectPath, null)

  const cwd = 'C:/proj/alpha'
  const before = reqRes('GET', { url: `${PATH_SETTINGS_ROUTE}?cwd=${encodeURIComponent(cwd)}` })
  handle(before.req, before.res); await tick()
  const beforeBody = JSON.parse(before.res.body)
  assert.equal(beforeBody.currentCwd, cwd)
  assert.equal(beforeBody.currentProjectPath, 'D:/global/proj', 'cwd 无匹配时回退全局项目路径')

  // POST 整份覆盖,随后 GET 读到新值。
  const write = reqRes('POST', { body: JSON.stringify({
    serverDist: 'D:/mcp/dist/index.js',
    projects: [{ cwd, projectPath: 'C:/proj/alpha' }, { cwd: 'C:/proj/beta', projectPath: 'C:/proj/beta' }],
  }) })
  handle(write.req, write.res); await tick()
  assert.equal(JSON.parse(write.res.body).ok, true)
  assert.equal(store.getServerDist(), 'D:/mcp/dist/index.js')
  assert.equal(store.getProjectPath(cwd), 'C:/proj/alpha')
  assert.equal(store.getProjectPath('C:/proj/beta'), 'C:/proj/beta')
  assert.equal(store.getProjectPath('C:/proj/gamma'), undefined, '未配置 cwd 无匹配')

  const after = reqRes('GET', { url: `${PATH_SETTINGS_ROUTE}?cwd=${encodeURIComponent(cwd)}` })
  handle(after.req, after.res); await tick()
  const afterBody = JSON.parse(after.res.body)
  assert.equal(afterBody.serverDist, 'D:/mcp/dist/index.js')
  assert.equal(afterBody.projects.length, 2)
  assert.equal(afterBody.currentProjectPath, 'C:/proj/alpha')
})

test('POST:整份覆盖增删改;非法条目被丢弃', async () => {
  const store = tempStore()
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: store })
  const handle = ctx.captured[0]!.handler
  const w1 = reqRes('POST', { body: JSON.stringify({ serverDist: 'S', projects: [{ cwd: 'A', projectPath: 'A1' }, { cwd: 'B', projectPath: 'B1' }] }) })
  handle(w1.req, w1.res); await tick()
  assert.equal(store.getProjectPath('A'), 'A1')
  assert.equal(store.getProjectPath('B'), 'B1')
  const w2 = reqRes('POST', { body: JSON.stringify({
    serverDist: 'S2',
    projects: [
      { cwd: 'A', projectPath: 'A2' },
      { cwd: '', projectPath: 'x' },
      { cwd: 'D', projectPath: '' },
      { cwd: 'C', projectPath: 'C1' },
    ],
  }) })
  handle(w2.req, w2.res); await tick()
  assert.equal(store.getServerDist(), 'S2')
  assert.equal(store.getProjectPath('A'), 'A2', '覆盖生效')
  assert.equal(store.getProjectPath('B'), undefined, '删除生效')
  assert.equal(store.getProjectPath('C'), 'C1')
  assert.equal(store.getConfig().projects.length, 2, '非法条目被丢弃:cwd 为空/路径为空')
})

test('POST:pathStore 缺席时 400', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, {})
  const r = reqRes('POST', { body: JSON.stringify({ serverDist: 'S', projects: [] }) })
  ctx.captured[0]!.handler(r.req, r.res); await tick()
  assert.equal(r.res.statusCode, 400)
})

test('校验:非 GET/POST → 405', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  const r = reqRes('PUT', { body: '{}' })
  ctx.captured[0]!.handler(r.req, r.res); await tick()
  assert.equal(r.res.statusCode, 405)
})

test('校验:非法 JSON → 400;超 8MiB → 413', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  const handle = ctx.captured[0]!.handler
  const bad = reqRes('POST', { body: '{oops' })
  handle(bad.req, bad.res); await tick()
  assert.equal(bad.res.statusCode, 400)
  const huge = reqRes('POST', { body: 'x'.repeat(9 * 1024 * 1024) })
  handle(huge.req, huge.res); await tick()
  assert.equal(huge.res.statusCode, 413)
})

test('围栏:跨源 Origin 拒绝;同源允许', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  const handle = ctx.captured[0]!.handler
  for (const [origin, code] of [['http://evil.example', 403], [undefined, 200], ['http://127.0.0.1:3080', 200]] as const) {
    const r = reqRes('GET', { origin })
    handle(r.req, r.res); await tick()
    assert.equal(r.res.statusCode || 200, code, `origin=${origin ?? '(none)'}`)
  }
})

test('围栏:Host 白名单 + rebinding 拒绝', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  const handle = ctx.captured[0]!.handler
  for (const [origin, host, code] of [
    ['https://evil.com', 'evil.com', 403],
    [undefined, 'example.com:3080', 403],
    [undefined, '127.0.0.1:3080', 200],
    ['http://localhost:3080', 'localhost:3080', 200],
  ] as const) {
    const r = reqRes('GET', { ...(origin !== undefined ? { origin } : {}), host })
    handle(r.req, r.res); await tick()
    assert.equal(r.res.statusCode || 200, code, `host=${host}`)
  }
})

test('围栏:sec-fetch-site 第三层(跨站拒绝,同源/none/缺省放行)', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  const handle = ctx.captured[0]!.handler
  for (const [site, code] of [['cross-site', 403], ['same-site', 403], ['same-origin', 200], ['none', 200]] as const) {
    const r = reqRes('GET', { extraHeaders: { 'sec-fetch-site': site } })
    handle(r.req, r.res); await tick()
    assert.equal(r.res.statusCode || 200, code, `sec-fetch-site=${site}`)
  }
})

test('围栏:peer 回环校验(非回环/缺失/空地址均拒绝)', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  const handle = ctx.captured[0]!.handler
  for (const [remote, code] of [['192.168.1.5', 403], ['127.0.0.1', 200], ['::ffff:127.0.0.1', 200], ['', 403]] as const) {
    const r = reqRes('GET', { remoteAddress: remote })
    handle(r.req, r.res); await tick()
    assert.equal(r.res.statusCode || 200, code, `peer=${remote === '' ? '(empty)' : remote}`)
  }
  const missing = reqRes('GET', { omitSocket: true })
  handle(missing.req, missing.res); await tick()
  assert.equal(missing.res.statusCode, 403)
})

test('500:固定文案不泄漏底层错误;原始错误经 warn 上报', async () => {
  const warns: string[] = []
  const ctx = fakeCtx()
  // 用会抛错的 pathStore 触发 handler 内异常。
  const broken = {
    getConfig: () => { throw new Error('SECRET internal detail C:\\Users\\x\\key.json') },
    getProjectPath: () => undefined,
  } as unknown as PathStore
  mountPathSettingsRoute(ctx as never, { pathStore: broken, warn: message => { warns.push(message) } })
  const r = reqRes('GET')
  ctx.captured[0]!.handler(r.req, r.res); await tick(); await tick()
  assert.equal(r.res.statusCode, 500)
  const payload = JSON.parse(r.res.body) as { error: { message: string } }
  assert.equal(payload.error.message, '内部错误(详情已记录到 host 日志)')
  assert.ok(!r.res.body.includes('SECRET'), '底层错误细节不进 HTTP 响应')
  assert.equal(warns.length, 1)
  assert.ok(warns[0]!.includes('SECRET'), '原始错误经 warn 走 host 日志')
})

test('readBody:提前 close 不悬挂(500 返回)', async () => {
  const ctx = fakeCtx()
  mountPathSettingsRoute(ctx as never, { pathStore: tempStore() })
  const handlers = new Map<string, Array<(arg?: unknown) => void>>()
  const req = {
    method: 'POST',
    url: PATH_SETTINGS_ROUTE,
    headers: { host: '127.0.0.1:3080' },
    socket: { remoteAddress: '127.0.0.1' },
    on: (event: string, cb: (arg?: unknown) => void) => {
      const list = handlers.get(event) ?? []
      list.push(cb)
      handlers.set(event, list)
    },
  }
  let ended = false
  const res = { statusCode: 0, body: '', setHeader: () => {}, end: (payload?: string) => { ended = true; res.body = payload ?? '' } }
  ctx.captured[0]!.handler(req, res)
  await tick()
  for (const cb of handlers.get('close') ?? []) cb()
  await tick(); await tick()
  assert.equal(ended, true)
  assert.equal(res.statusCode, 500)
})

test('PathStore:normalizeProjects 去重、上限 100、字段校验', () => {
  const out = normalizeProjects([
    { cwd: 'X', projectPath: 'P1' },
    { cwd: 'X', projectPath: 'P2' },
    { cwd: '', projectPath: 'P' },
    { cwd: 'Y', projectPath: '' },
    'z',
    null,
    { cwd: 'Z', projectPath: 'P3' },
  ])
  assert.deepEqual(out, [{ cwd: 'X', projectPath: 'P2' }, { cwd: 'Z', projectPath: 'P3' }])
  const many = Array.from({ length: 150 }, (_, i) => ({ cwd: `c${i}`, projectPath: `p${i}` }))
  assert.equal(normalizeProjects(many).length, 100, '上限 100')
})

test('PathStore:cleanPath 剥除首尾成对引号并 trim', () => {
  assert.equal(cleanPath('  "D:\\proj\\godot\\project.godot"  '), 'D:\\proj\\godot\\project.godot')
  assert.equal(cleanPath("'D:\\proj'"), 'D:\\proj')
  assert.equal(cleanPath('  D:\\proj  '), 'D:\\proj')
  assert.equal(cleanPath('"unbalanced'), '"unbalanced')
  assert.equal(cleanPath('"a"b"'), 'a"b')
  const out = normalizeProjects([{ cwd: '"D:\\proj"', projectPath: '"D:\\proj\\godot\\project.godot"' }])
  assert.deepEqual(out, [{ cwd: 'D:\\proj', projectPath: 'D:\\proj\\godot' }])
})

test('PathStore:惰性加载与原子持久化(跨实例读回)', () => {
  const store = tempStore()
  const a = store
  a.setConfig({ serverDist: 'D:/mcp/dist/index.js', projects: [{ cwd: 'C:/proj', projectPath: 'C:/proj' }] })
  const file = (a as unknown as { file: string }).file
  const b = new PathStore(file)
  assert.equal(b.getServerDist(), 'D:/mcp/dist/index.js')
  assert.equal(b.getProjectPath('C:/proj'), 'C:/proj')
  b.setProjects([{ cwd: 'C:/proj', projectPath: 'C:/proj' }, { cwd: 'C:/other', projectPath: 'C:/other' }])
  assert.equal(b.getServerDist(), 'D:/mcp/dist/index.js', 'setProjects 保留 serverDist')
  assert.equal(b.getProjectPath('C:/other'), 'C:/other')
})

test('PathStore:外部进程改写文件后按 mtime 热生效(CLI install 场景)', async () => {
  const dir = join(tmpdir(), `godot-paths-hot-${Date.now()}-${Math.random().toString(16).slice(2)}`)
  mkdirSync(dir, { recursive: true })
  try {
    const file = join(dir, 'paths.json')
    const owned = new PathStore(file)
    // 先读到旧值(建立内存缓存与 mtime 基线)。
    assert.equal(owned.getServerDist(), undefined)

    // 模拟 CLI 在另一个进程里写文件。mtime 分辨率可能较粗:显式把 mtime 推后。
    writeFileSync(file, JSON.stringify({ serverDist: 'D:/cli/shim.exe', projects: [{ cwd: 'C:/game', projectPath: 'C:/game' }] }))
    const future = new Date(Date.now() + 5_000)
    utimesSync(file, future, future)

    assert.equal(owned.getServerDist(), 'D:/cli/shim.exe', '外部写入应被读到')
    assert.equal(owned.getProjectPath('C:/game'), 'C:/game')

    // 自己写回后 mtime 基线同步更新,后续读取不会回退到旧内容。
    owned.setServerDist('D:/plugin/shim.exe')
    assert.equal(owned.getServerDist(), 'D:/plugin/shim.exe')
    assert.equal(owned.getServerDist(), 'D:/plugin/shim.exe', '连续读取稳定(不反复重读)')
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})
