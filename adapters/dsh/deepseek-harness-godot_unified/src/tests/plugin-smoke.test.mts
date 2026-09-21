// host 插件冒烟:注入策略(默认不注入/Godot preset/原版模式)、config 覆盖、
// 重复注册容错、工作台装配与 teardown。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { apply, inject, name, parsePluginConfig, SETTINGS_NAMESPACE, GODOT_PRESET_ID } from '../index.js'
import { SECTION_NAME } from '../constants.js'

interface SectionRecord { name: string; order?: number; text: () => string }

interface FakeAgent {
  presetId?: string
  restrictCalls: Array<{ deny?: string[] }>
  scopedSections: SectionRecord[]
}

function makeCtx(options: { alwaysReject?: boolean; agents?: FakeAgent[]; settings?: Record<string, unknown>; listAgents?: boolean } = {}) {
  const sections: SectionRecord[] = []
  const effects: Array<() => void> = []
  const listeners = new Map<string, Array<(payload: unknown) => void>>()

  const promptService = {
    section: (input: SectionRecord) => {
      // alwaysReject 模拟"该名称已由先到实例注册"的服务端拒绝(双行并存)。
      if (options.alwaysReject === true || sections.some((s) => s.name === input.name)) {
        throw new Error('section already registered')
      }
      sections.push(input)
      let disposed = false
      return () => {
        if (disposed) return
        disposed = true
        sections.splice(sections.indexOf(input), 1)
      }
    },
  }

  const agentShapes = (options.agents ?? []).map((agent) => ({
    id: `agent-${agent.presetId ?? 'plain'}`,
    restrictCalls: agent.restrictCalls,
    scopedSections: agent.scopedSections,
    ctx: {
      presetId: agent.presetId,
      tools: {
        restrict: (filter: { deny?: string[] }) => {
          agent.restrictCalls.push(filter)
          return () => {}
        },
      },
      get: (service: string) => (service === 'systemPrompt' ? promptService : undefined),
    },
    session: { header: { id: `session-${agent.presetId ?? 'plain'}` } },
  }))

  return {
    sections,
    effects,
    listeners,
    agents: agentShapes,
    emit(name: string, payload?: unknown): void {
      for (const handler of listeners.get(name) ?? []) handler(payload)
    },
    get: (service: string) => {
      if (service === 'systemPrompt') return promptService
      if (service === 'tools') return {
        schemas: () => [
          { name: 'godot_discover_tools' },
          { name: 'godot_scene_get_tree' },
          { name: 'read' },
        ],
      }
      if (service === 'settings') {
        return {
          register: (_namespace: string, _schema: unknown, opts: { base: Record<string, unknown> }) => ({
            get: () => (options.settings ?? opts.base),
            watch: (_listener: (next: Record<string, unknown>) => void) => {
              listeners.set('settings/watch', [...(listeners.get('settings/watch') ?? []), _listener])
              return () => {}
            },
          }),
        }
      }
      if (service === 'agents') return { list: () => (options.listAgents === false ? [] : agentShapes) }
      if (service === 'agentPresets') return { composedPreset: (agentCtx: { presetId?: string }) => agentCtx?.presetId }
      return undefined
    },
    effect: (execute: () => (() => void) | void) => {
      const ret = execute()
      effects.push(typeof ret === 'function' ? ret : () => {})
    },
    on: (event: string, handler: (payload: unknown) => void) => {
      listeners.set(event, [...(listeners.get(event) ?? []), handler])
    },
    inject: () => {},
  }
}

test('模块导出形状', () => {
  assert.equal(name, 'deepseek-harness-godot_unified')
  assert.deepEqual(inject, ['systemPrompt', 'tools', 'settings', 'agents'])
  assert.equal(SETTINGS_NAMESPACE, 'godot')
  assert.equal(GODOT_PRESET_ID, 'godot')
})

test('apply:默认(未开原版模式)不注册全局 section', () => {
  const ctx = makeCtx()
  apply(ctx as never)
  assert.equal(ctx.sections.length, 0)
})

test('apply:原版模式注册全局 section,默认 serverName=godot;teardown 注销', () => {
  const ctx = makeCtx({ settings: { injectLegacyMode: true } })
  apply(ctx as never)
  assert.equal(ctx.sections.length, 1)
  assert.equal(ctx.sections[0]!.name, SECTION_NAME)
  assert.ok(ctx.sections[0]!.text().includes('godot_discover_tools'))
  assert.ok(!ctx.sections[0]!.text().includes('myserver_'))
  // fiber teardown 执行后 section 注销。
  for (const dispose of ctx.effects) dispose()
  assert.equal(ctx.sections.length, 0)
})

test('apply:原版模式下 config.serverName 覆盖工具前缀', () => {
  const ctx = makeCtx({ settings: { injectLegacyMode: true } })
  apply(ctx as never, { serverName: 'gd47' })
  assert.ok(ctx.sections[0]!.text().includes('gd47_discover_tools'))
})

test('apply:重复注册(双行并存)被容错,不抛异常', () => {
  const rejected = makeCtx({ alwaysReject: true, settings: { injectLegacyMode: true } })
  assert.doesNotThrow(() => apply(rejected as never))
  // 被拒实例未注册成功;teardown effect 仍注册但内部为 no-op。
  assert.equal(rejected.sections.length, 0)
  assert.doesNotThrow(() => rejected.effects[0]!())
})

test('apply:agent/created 按 preset 分流——Godot preset 得提示词,其余被 deny', () => {
  const ctx = makeCtx({
    agents: [{ presetId: 'godot', restrictCalls: [], scopedSections: [] }, { presetId: 'standard', restrictCalls: [], scopedSections: [] }],
  })
  apply(ctx as never)
  // 默认策略:全局无 section;Godot preset agent 注册 scoped section(fake 环境里
  // agent.ctx.get('systemPrompt') 即全局 promptService,注册落在同一数组),
  // standard agent 不注册提示词、只被 restrict deny。
  assert.equal(ctx.sections.length, 1)
  assert.equal(ctx.sections[0]!.name, SECTION_NAME)
  assert.deepEqual(ctx.agents[0]!.restrictCalls, [])
  assert.equal(ctx.agents[1]!.restrictCalls.length, 1)
})

test('apply:agent/created 的 deny 名单只含 godot_* 工具;重复 created 幂等跳过', () => {
  // fresh agent 不在 apply 时的 list 中:走 agent/created 首次安装。
  const ctx = makeCtx({ agents: [{ presetId: 'standard', restrictCalls: [], scopedSections: [] }], listAgents: false })
  apply(ctx as never)
  assert.equal(ctx.sections.length, 0)
  ctx.emit('agent/created', { agent: ctx.agents[0] })
  const calls = restrictedOf(ctx, 0)
  assert.equal(calls.length, 1)
  assert.deepEqual(calls[0]!.deny, ['godot_discover_tools', 'godot_scene_get_tree'])
  // 非 Godot agent 没有提示词 section。
  assert.equal(ctx.sections.length, 0)
  // 同一名单的重复 created 幂等跳过(不重复 restrict,避免 tools/change 抖动)。
  ctx.emit('agent/created', { agent: ctx.agents[0] })
  assert.equal(calls.length, 1)
  assert.equal(ctx.sections.length, 0)
})

function restrictedOf(ctx: ReturnType<typeof makeCtx>, index: number): Array<{ deny?: string[] }> {
  return (ctx.agents[index] as unknown as { restrictCalls: Array<{ deny?: string[] }> }).restrictCalls
}

test('apply:settings watch 切原版模式后全局 section 出现', () => {
  const ctx = makeCtx()
  apply(ctx as never)
  assert.equal(ctx.sections.length, 0)
  for (const listener of ctx.listeners.get('settings/watch') ?? []) {
    listener({ injectLegacyMode: true, promptGuidance: true, zhPrompt: false })
  }
  assert.equal(ctx.sections.length, 1)
  assert.equal(ctx.sections[0]!.name, SECTION_NAME)
})

test('apply:systemPrompt 服务缺失时降级为 warn,不抛异常', () => {
  const missing = { get: () => undefined, effect: () => {}, on: () => {} }
  assert.doesNotThrow(() => apply(missing as never))
})

test('parsePluginConfig:默认与覆盖', () => {
  assert.deepEqual(parsePluginConfig({}), { serverName: 'godot', godotMcpRoot: undefined, projectPath: undefined })
  assert.equal(parsePluginConfig({ unsafe: true }).unsafe, true)
  assert.equal(parsePluginConfig({}).unsafe, undefined)
  assert.deepEqual(
    parsePluginConfig({ serverName: 'gd47', godotMcpRoot: 'D:/ws/GodotMCP', projectPath: 'D:/games/x' }),
    { serverName: 'gd47', godotMcpRoot: 'D:/ws/GodotMCP', projectPath: 'D:/games/x' },
  )
  // 非法形状回退默认,不抛错。
  assert.deepEqual(parsePluginConfig({ serverName: 42, godotMcpRoot: '' }), { serverName: 'godot', godotMcpRoot: undefined, projectPath: undefined })
})

// 工作台装配:tools 服务在场才装配;webServer 经 inject 延迟挂载;effect 收集 teardown disposer。
function makeWorkbenchCtx(hasTools: boolean) {
  const sections: SectionRecord[] = []
  const effects: Array<() => void> = []
  const routes: Array<{ path: string; dispose: () => void }> = []
  const registered: string[] = []
  const state: { injectDeps?: string[]; injectCb?: () => void } = {}
  const webServer = {
    register: (opt: { path: string }) => {
      const rec = { path: opt.path, dispose: () => { const i = routes.indexOf(rec); if (i >= 0) routes.splice(i, 1) } }
      routes.push(rec)
      return rec.dispose
    },
  }
  const tools = hasTools
    ? {
        schemas: () => [
          { name: 'godot_discover_tools' },
          { name: 'godot_scene_get_tree' },
        ],
        register: (definition: Record<string, unknown>) => {
          registered.push(String(definition.name))
          return () => {
            const idx = registered.indexOf(String(definition.name))
            if (idx >= 0) registered.splice(idx, 1)
          }
        },
        get: () => undefined,
        execute: async () => ({ isError: false }),
      }
    : undefined
  return {
    sections,
    effects,
    routes,
    registered,
    state,
    get: (service: string) => {
      if (service === 'systemPrompt') return { section: (input: SectionRecord) => { sections.push(input); return () => { sections.splice(sections.indexOf(input), 1) } } }
      if (service === 'tools') return tools
      if (service === 'webServer') return webServer
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

test('apply:tools 在场时经 inject 挂载工作台路由;teardown 注销', () => {
  const ctx = makeWorkbenchCtx(true)
  apply(ctx as never)
  assert.deepEqual(ctx.state.injectDeps, ['webServer'])
  ctx.state.injectCb!()
  assert.equal(ctx.routes.length, 8)
  for (const dispose of ctx.effects) dispose()
  assert.equal(ctx.routes.length, 0)
})

test('apply:tools 缺席时不装配工作台路由', () => {
  const ctx = makeWorkbenchCtx(false)
  apply(ctx as never)
  assert.equal(ctx.state.injectDeps, undefined)
  assert.equal(ctx.routes.length, 0)
})
