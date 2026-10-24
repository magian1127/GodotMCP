// host 插件冒烟:官方 MCP 挂载策略(仅 Godot preset 会话 / 原版模式共享连接)、
// 提示词 section、幂等与可逆 teardown、路径设置路由装配。
//
// 测试不碰真实用户目录:$DSH_HOME 指到临时目录,且必须在 import ../index.js
// 之前设置——host 侧 PathStore 的默认文件路径在模块求值时计算。
import assert from 'node:assert/strict'
import test from 'node:test'
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// 隔离运行环境:
// - `$DSH_HOME` 指到临时目录,host 侧 PathStore 的 paths.json 不落真实用户目录;
// - 在临时 profile 里放一个**桩** `@deepseek-ai/dsh-mcp-client`(CJS),
//   让 `loadMcpClient()` 走真实的 profile 解析路径拿到插件本体。
// 两件事都必须在 import ../index.js 之前完成(profile 预载在模块求值时发生)。
const dshHome = mkdtempSync(join(tmpdir(), 'godot-dsh-'))
process.env.DSH_HOME = dshHome
const profileDir = join(dshHome, 'profiles', 'web')
const stubDir = join(profileDir, 'node_modules', '@deepseek-ai', 'dsh-mcp-client')
mkdirSync(stubDir, { recursive: true })
writeFileSync(join(profileDir, 'package.json'), JSON.stringify({ name: 'dsh-test-profile', private: true }))
writeFileSync(join(stubDir, 'package.json'), JSON.stringify({
  name: '@deepseek-ai/dsh-mcp-client',
  version: '0.0.0-test',
  main: 'index.js',
}))
writeFileSync(join(stubDir, 'index.js'), [
  "module.exports = {",
  "  name: 'mcp-client',",
  "  inject: ['tools'],",
  '  // 测试桩:官方 schema 是 schemastery 工厂,这里只需接受 Config 形状。',
  '  Config: (input) => input,',
  '  apply() {},',
  '}',
].join('\n'))

const { apply, inject, name, parsePluginConfig, Config, GODOT_PRESET_ID } = await import('../index.js')
const { SECTION_NAME, SECTION_ORDER } = await import('../constants.js')

interface SectionRecord { name: string; order?: number; text: () => string; disposed: boolean }

interface FakeFiber {
  disposed: boolean
  dispose(): Promise<void>
  then(onFulfilled?: null | (() => void), onRejected?: null | ((error: unknown) => void)): Promise<unknown>
}

function makeFiber(): FakeFiber {
  const fiber: FakeFiber = {
    disposed: false,
    dispose(): Promise<void> {
      fiber.disposed = true
      return Promise.resolve()
    },
    then(onFulfilled, onRejected): Promise<unknown> {
      return Promise.resolve().then(onFulfilled ?? undefined, onRejected ?? undefined)
    },
  }
  return fiber
}

interface FakeAgent {
  id: string
  presetId?: string
  session: { header: { id: string; cwd?: string } }
  ctx: {
    presetId?: string
    plugin(plugin: unknown, config?: unknown): FakeFiber
    get(service: string): unknown
    systemPrompt?: unknown
  }
}

interface HostPluginRecord { plugin: unknown; config: Record<string, unknown>; fiber: FakeFiber }
interface AgentPluginRecord { agent: string; plugin: unknown; config: Record<string, unknown>; fiber: FakeFiber }

interface CtxOptions {
  settings?: Record<string, unknown>
  agents?: Array<{ presetId?: string; id?: string; cwd?: string }>
  listAgents?: boolean
  alwaysReject?: boolean
  noAgentsService?: boolean
  webServer?: unknown
}

function makeCtx(options: CtxOptions = {}) {
  const sections: SectionRecord[] = []
  const effects: Array<() => void> = []
  const listeners = new Map<string, Array<(payload?: unknown) => unknown>>()
  const hostPlugins: HostPluginRecord[] = []
  const agentPlugins: AgentPluginRecord[] = []
  const routes: Array<{ path: string; dispose: () => void }> = []
  const injectState: { deps?: string[]; callback?: () => void } = {}

  const configState: Record<string, unknown> = {
    injectLegacyMode: false,
    promptGuidance: true,
    zhPrompt: false,
    godotServerDist: '',
    godotProjectPath: '',
    ...(options.settings ?? {}),
  }
  const configRef: Record<string, unknown> = {}
  for (const key of Object.keys(configState)) {
    configRef[key] = { get: () => configState[key] }
  }

  const promptService = {
    section(input: { name: string; order?: number; text: () => string }): () => void {
      // alwaysReject 模拟"该名称已由先到实例注册"的服务端拒绝(双行并存)。
      if (options.alwaysReject === true) throw new Error('section already registered')
      const record: SectionRecord = { ...input, disposed: false }
      sections.push(record)
      return () => {
        if (record.disposed) return
        record.disposed = true
        const index = sections.indexOf(record)
        if (index >= 0) sections.splice(index, 1)
      }
    },
  }

  const agentShapes: FakeAgent[] = (options.agents ?? []).map((agent, index) => {
    const id = agent.id ?? `sess-${index}`
    const shape: FakeAgent = {
      id,
      presetId: agent.presetId,
      session: { header: { id, ...(agent.cwd === undefined ? {} : { cwd: agent.cwd }) } },
      ctx: {
        presetId: agent.presetId,
        get: (service: string) => (service === 'systemPrompt' ? promptService : undefined),
        systemPrompt: promptService,
        plugin: (plugin: unknown, config?: unknown): FakeFiber => {
          const fiber = makeFiber()
          agentPlugins.push({ agent: id, plugin, config: (config ?? {}) as Record<string, unknown>, fiber })
          return fiber
        },
      },
    }
    return shape
  })

  const ctx = {
    sections,
    effects,
    listeners,
    hostPlugins,
    agentPlugins,
    routes,
    injectState,
    agents: agentShapes,
    configRef,
    fireVolatile(patch: Record<string, unknown>): void {
      Object.assign(configState, patch)
      for (const handler of listeners.get('loader/volatile-update') ?? []) handler(undefined)
    },
    async emit(event: string, payload?: unknown): Promise<void> {
      for (const handler of listeners.get(event) ?? []) await handler(payload)
    },
    get(service: string): unknown {
      if (service === 'systemPrompt') return promptService
      if (service === 'agents') {
        return options.noAgentsService === true
          ? undefined
          : { list: () => (options.listAgents === false ? [] : agentShapes) }
      }
      if (service === 'agentPresets') {
        return { composedPreset: (agentCtx?: { presetId?: string }) => agentCtx?.presetId }
      }
      if (service === 'webServer') return options.webServer
      return undefined
    },
    plugin(plugin: unknown, config?: unknown): FakeFiber {
      const fiber = makeFiber()
      hostPlugins.push({ plugin, config: (config ?? {}) as Record<string, unknown>, fiber })
      return fiber
    },
    effect(execute: () => (() => void) | void): void {
      const ret = execute()
      effects.push(typeof ret === 'function' ? ret : () => {})
    },
    on(event: string, handler: (payload?: unknown) => unknown): void {
      listeners.set(event, [...(listeners.get(event) ?? []), handler])
    },
    inject(deps: string[], callback: () => void): void {
      injectState.deps = deps
      injectState.callback = () => {
        // 真实 Cordis 语义:webServer 就绪后回调收到的 ctx 携带该服务。
        callback()
      }
    },
  }
  return ctx
}

const MOUNTED_SETTINGS = {
  godotServerDist: 'D:/gmcp/godot-mcp-shim.exe',
  godotProjectPath: 'D:/game',
}

test('模块导出形状', () => {
  assert.equal(name, 'deepseek-harness-godot_unified')
  assert.deepEqual(inject, ['systemPrompt', 'agents'])
  // Config 为宿主 0.1.7+ 投影表单的依据;本机 profile 有 schemastery 时为可调用
  // schema,不可用时导出 undefined(绝不能是 null,会崩掉宿主整页设置)。
  assert.ok(Config === undefined || typeof Config === 'object' || typeof Config === 'function')
  assert.equal(GODOT_PRESET_ID, 'godot')
})

test('默认(未配置 server 入口):Godot 会话不挂载,只注入"未配置"提示词', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }] })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })
  assert.equal(ctx.agentPlugins.length, 0)
  assert.equal(ctx.sections.length, 1)
  assert.equal(ctx.sections[0]!.name, SECTION_NAME)
  assert.equal(ctx.sections[0]!.order, SECTION_ORDER)
  const text = ctx.sections[0]!.text()
  assert.ok(text.includes('Godot MCP is not connected'), '未配置时说明原因')
  assert.ok(text.includes('mcp__godot__'), '给出工具名形状')
})

test('非 Godot preset 会话:不挂载、无提示词(不是 deny,是根本没有)', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'standard', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })
  assert.equal(ctx.agentPlugins.length, 0)
  assert.equal(ctx.sections.length, 0)
})

test('Godot preset 会话:挂载官方 mcp-client(stdio + 项目路径 env + 会话 cwd)', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })

  assert.equal(ctx.agentPlugins.length, 1)
  const mounted = ctx.agentPlugins[0]!
  assert.equal(mounted.agent, 's1')
  assert.equal((mounted.plugin as { name?: string }).name, 'mcp-client')
  assert.deepEqual(mounted.config, {
    transport: 'stdio',
    serverName: 'godot',
    command: 'D:/gmcp/godot-mcp-shim.exe',
    args: [],
    env: { GODOT_MCP_PROJECT_PATH: 'D:/game' },
    cwd: 'D:/game',
    toolCallTimeoutMs: 60_000,
    failOnStartupError: false,
  })
  // 提示词 section 与工具一起出现,且用官方命名。
  assert.equal(ctx.sections.length, 1)
  assert.ok(ctx.sections[0]!.text().includes('mcp__godot__discover_tools'))
})

test('同一会话重复 agent/created(不同对象)幂等:不重复挂载', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })
  // 同一 sessionId 的另一个 agent 对象(真实运行时会这样)。
  await ctx.emit('agent/created', { agent: { ...ctx.agents[0] } })
  assert.equal(ctx.agentPlugins.length, 1)
  assert.equal(ctx.sections.length, 1)
})

test('原版模式:host 作用域挂一个共享连接 + 全局 section,不再按会话挂', async () => {
  const ctx = makeCtx({
    agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }],
    settings: { ...MOUNTED_SETTINGS, injectLegacyMode: true },
  })
  apply(ctx as never, ctx.configRef)
  // apply 时即挂(host 作用域)。
  assert.equal(ctx.hostPlugins.length, 1)
  assert.equal(ctx.sections.length, 1)
  assert.equal(ctx.sections[0]!.name, SECTION_NAME)

  await ctx.emit('agent/created', { agent: ctx.agents[0] })
  assert.equal(ctx.agentPlugins.length, 0, '原版模式不再按会话重复挂载')

  // 关闭原版模式:共享连接被拆,Godot 会话改为按会话挂载。
  ctx.fireVolatile({ injectLegacyMode: false })
  assert.equal(ctx.hostPlugins[0]!.fiber.disposed, true)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })
  assert.equal(ctx.agentPlugins.length, 1)
  assert.equal(ctx.sections.filter(section => section.name === SECTION_NAME).length, 1, '全局 section 已拆,scoped section 接替')
})

test('原版模式 + 未配置 server 入口:全局提示词是"未配置"而不是完整工作流', () => {
  const ctx = makeCtx({ settings: { injectLegacyMode: true } })
  apply(ctx as never, ctx.configRef)
  assert.equal(ctx.sections.length, 1)
  assert.ok(ctx.sections[0]!.text().includes('Godot MCP is not connected'), '没有工具时不得教模型调用工具')
  assert.equal(ctx.hostPlugins.length, 0, '入口为空时不起共享连接')

  // 配置后文本动态切回完整工作流,并挂上共享连接。
  ctx.fireVolatile({ godotServerDist: 'D:/gmcp/godot-mcp-shim.exe', godotProjectPath: 'D:/game' })
  assert.equal(ctx.hostPlugins.length, 1)
  assert.ok(ctx.sections[0]!.text().includes('Godot workflow'))
})

test('空白会话换预设:切到 Godot 挂载,切走卸载并移除提示词', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'standard', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)

  ctx.agents[0]!.ctx.presetId = 'godot'
  await ctx.emit('agent-preset/selected', 's1')
  assert.equal(ctx.agentPlugins.length, 1)
  assert.equal(ctx.sections.length, 1)

  ctx.agents[0]!.ctx.presetId = 'standard'
  await ctx.emit('agent-preset/selected', 's1')
  assert.equal(ctx.agentPlugins[0]!.fiber.disposed, true)
  assert.equal(ctx.sections.length, 0)
})

test('agent/disposed:卸载该会话的挂载与提示词', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })
  assert.equal(ctx.sections.length, 1)

  await ctx.emit('agent/disposed', { agent: ctx.agents[0] })
  assert.equal(ctx.agentPlugins[0]!.fiber.disposed, true)
  assert.equal(ctx.sections.length, 0)
})

test('插件 teardown:回收全部会话挂载与 section', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })

  for (const dispose of ctx.effects) dispose()
  assert.equal(ctx.agentPlugins[0]!.fiber.disposed, true)
  assert.equal(ctx.sections.length, 0)
})

test('设置变更(server 入口):旧连接卸载,新配置重挂', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })

  ctx.fireVolatile({ godotServerDist: 'D:/other/godot-mcp-shim.exe' })
  assert.equal(ctx.agentPlugins.length, 2)
  assert.equal(ctx.agentPlugins[0]!.fiber.disposed, true)
  assert.equal(ctx.agentPlugins[1]!.config.command, 'D:/other/godot-mcp-shim.exe')
})

test('zhPrompt 切换:文本函数动态生效,不重挂连接', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1', cwd: 'D:/game' }], settings: MOUNTED_SETTINGS })
  apply(ctx as never, ctx.configRef)
  await ctx.emit('agent/created', { agent: ctx.agents[0] })
  assert.ok(ctx.sections[0]!.text().includes('Godot workflow'))

  ctx.fireVolatile({ zhPrompt: true })
  assert.ok(ctx.sections[0]!.text().includes('Godot 工作流'))
  assert.equal(ctx.agentPlugins.length, 1, '只改文案不重挂')
})

test('section 重复注册(双行并存)被容错,不抛异常', async () => {
  const ctx = makeCtx({ agents: [{ presetId: 'godot', id: 's1' }], settings: MOUNTED_SETTINGS, alwaysReject: true })
  assert.doesNotThrow(() => apply(ctx as never, ctx.configRef))
  assert.equal(ctx.sections.length, 0)
  for (const dispose of ctx.effects) assert.doesNotThrow(dispose)
})

test('systemPrompt / agents 服务缺失时降级为 warn,不抛异常', () => {
  const missing = { get: () => undefined, effect: () => {}, on: () => {}, plugin: () => makeFiber() }
  assert.doesNotThrow(() => apply(missing as never))
})

test('parsePluginConfig:只保留被消费的 serverName;非法形状回退默认', () => {
  assert.deepEqual(parsePluginConfig({}), { serverName: 'godot' })
  assert.deepEqual(parsePluginConfig({ serverName: 'gd47' }), { serverName: 'gd47' })
  // v0.4 的 godotMcpRoot/projectPath/unsafe 已删除(host 侧从未消费)。
  assert.deepEqual(
    parsePluginConfig({ serverName: 'gd47', godotMcpRoot: 'D:/ws/GodotMCP', projectPath: 'D:/games/x', unsafe: true }),
    { serverName: 'gd47' },
  )
  assert.deepEqual(parsePluginConfig({ serverName: 42 }), { serverName: 'godot' })
  assert.deepEqual(parsePluginConfig({ serverName: '   ' }), { serverName: 'godot' })
})

// 路径设置路由装配:webServer 经 inject 延迟挂载,1 条 GET/POST 合并路由;effect 收集 teardown disposer。
function makePathRouteCtx(hasWebServer: boolean) {
  const effects: Array<() => void> = []
  const routes: Array<{ path: string; dispose: () => void }> = []
  const state: { injectDeps?: string[]; injectCb?: () => void } = {}
  const webServer = {
    register: (opt: { path: string }) => {
      const rec = { path: opt.path, dispose: () => { const i = routes.indexOf(rec); if (i >= 0) routes.splice(i, 1) } }
      routes.push(rec)
      return rec.dispose
    },
  }
  return {
    effects,
    routes,
    state,
    get: (service: string) => {
      if (service === 'systemPrompt') return { section: () => () => {} }
      if (service === 'agents') return { list: () => [] }
      if (service === 'webServer') return hasWebServer ? webServer : undefined
      return undefined
    },
    // 真实 Cordis 语义:execute 立即执行,返回函数为 teardown disposer。
    effect: (execute: () => (() => void) | void) => {
      const ret = execute()
      effects.push(typeof ret === 'function' ? ret : () => {})
    },
    on: () => {},
    inject: (deps: string[], cb: () => void) => { state.injectDeps = deps; state.injectCb = cb },
  }
}

test('apply:webServer 在场时经 inject 挂载路径设置路由;teardown 注销', () => {
  const ctx = makePathRouteCtx(true)
  apply(ctx as never, {})
  assert.deepEqual(ctx.state.injectDeps, ['webServer'])
  ctx.state.injectCb!()
  assert.equal(ctx.routes.length, 1)
  assert.deepEqual(ctx.routes.map(route => route.path), ['/godot-config/api/path-settings'])
  for (const dispose of ctx.effects) dispose()
  assert.equal(ctx.routes.length, 0)
})

test('apply:webServer 缺席时不挂路由(不报错)', () => {
  const ctx = makePathRouteCtx(false)
  assert.doesNotThrow(() => apply(ctx as never, {}))
  // inject 仍声明 webServer(服务晚到时回调会再触发),但 get 不到服务 → 零路由。
  assert.deepEqual(ctx.state.injectDeps, ['webServer'])
  ctx.state.injectCb!()
  assert.equal(ctx.routes.length, 0)
})
