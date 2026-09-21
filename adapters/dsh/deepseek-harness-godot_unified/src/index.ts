/**
 * deepseek-harness-godot_unified —— host 半边入口。
 *
 * 职责(v0.4,自研桥接 + 动态工具):
 * 1. **特殊桥接**:插件自己 spawn GodotMCP server(stdio)+ 内嵌极简 MCP
 *    client(`src/bridge/manager.ts` 的 GodotBridge),按**调用会话 cwd**
 *    解析并传参(GODOT_MCP_PROJECT_PATH 等 env)——不同会话工作区可以是
 *    不同 Godot 项目,桥接按需重启切换,绝不绑死目录。
 * 2. **动态工具**(不写死):工具清单来自 server 的 `tools/list`(真实
 *    schema/描述),插件挂载后异步预热一次清单并注册 `godot_*`
 *    门面;server 空闲关闭/崩溃后工具保持在册,调用时 ensureStarted 自愈。
 *    插件内**没有任何写死的工具清单**(无离线占位)。
 * 3. **注入管控**(hashline 同款 per-agent 表面模式):默认仅 Godot preset
 *    会话注入工具与提示词;`injectLegacyMode` 开启恢复全局注入。deny 的
 *    tools/change 增量补丁经 150ms 合并窗口,防 re-sync 事件风暴。
 * 4. **Godot 工作台 HTTP 路由**(tools 服务在场时装配):浏览/手动调用/
 *    状态/路径;目录缓存 + 在线判定防「浏览即起动 server」。
 *
 * 配置走设置卡片(`godot` 命名空间):注入三开关 + server dist/项目路径
 * 参考值;per-cwd 路径经 `$DSH_HOME/godot/paths.json`(工作台可维护)。
 */
import { GodotBridge, type McpToolDef } from './bridge/manager.js'
import { ToolsCacheStore } from './bridge/tools-cache.js'
import { UPSTREAM_TOOL_ZH } from './bridge/upstream-zh.js'
import { PKG, SECTION_NAME, SECTION_ORDER } from './constants.js'
import { sectionText } from './section-text.js'
import type {
  AgentPresetsShape,
  AgentShape,
  Disposer,
  HostContext,
  SettingsRegistrationShape,
  SettingsServiceShape,
  SystemPromptService,
  ToolsServiceShape,
} from './types.js'
import {
  godotToolNames, needsScopedPrompt, resolveSettings, resolveToolDescription, shouldHideAgent, toolFailureMessage,
  type GodotSettings,
} from './injection.js'
import { loadSchemastery } from './profile-modules.js'
import { ToolsCatalogueCache } from './workbench/handlers.js'
import { PathStore, projectPathToDir } from './workbench/path-store.js'
import { mountWorkbenchRoutes } from './workbench/routes.js'
import { WorkbenchExecutor } from './workbench/executor.js'

export const name = PKG
export const inject = ['systemPrompt', 'tools', 'settings', 'agents']

/** 设置命名空间(插件页配置表单经官方 settingsScope 读写)。 */
export const SETTINGS_NAMESPACE = 'godot'

/** Godot preset id(目录名 ~/.dsh/.agent-presets/godot;显示名 Godot)。 */
export const GODOT_PRESET_ID = 'godot'

/** 组合行 config 解析(兼容保留:serverName/unsafe 仍可由行覆盖;机器路径主要走设置/路径存储)。 */
export function parsePluginConfig(config: Record<string, unknown> = {}): {
  serverName: string; godotMcpRoot?: string; projectPath?: string; unsafe?: boolean
} {
  const str = (v: unknown): string | undefined => (typeof v === 'string' && v.trim().length > 0 ? v.trim() : undefined)
  return {
    serverName: str(config.serverName) ?? 'godot',
    godotMcpRoot: str(config.godotMcpRoot),
    projectPath: str(config.projectPath),
    ...(config.unsafe === true ? { unsafe: true } : {}),
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

/** tools/change 合并窗口(ms):门面 re-sync/注册逐工具发出事件,合并成一次处理。 */
const DENY_REFRESH_DEBOUNCE_MS = 150

export function apply(ctx: HostContext, config: Record<string, unknown> = {}): void {
  const { serverName, godotMcpRoot, unsafe } = parsePluginConfig(config)
  const toolPrefix = `${serverName}_`
  const warn = (message: string): void => { try { ctx.logger?.warn(message) } catch { /* 日志不可用不致命 */ } }

  const systemPrompt = ctx.get('systemPrompt') as SystemPromptService | undefined | null
  const promptService: SystemPromptService | undefined = systemPrompt ?? undefined

  // ---- 设置(schema 从 profile 解析;不可用时降级 base 默认,无设置 UI)。 ----
  const schemaFactory = loadSchemastery() as
    | { object: (shape: Record<string, unknown>) => unknown; boolean: () => { default(value: boolean): unknown }; string: () => { default(value: string): unknown } }
    | null
  let settings: SettingsRegistrationShape | undefined
  const SETTINGS_BASE = {
    injectLegacyMode: false,
    promptGuidance: true,
    zhPrompt: false,
    godotServerDist: '',
    godotProjectPath: '',
  }
  if (schemaFactory !== null && schemaFactory !== undefined && typeof schemaFactory.object === 'function') {
    try {
      const Config = schemaFactory.object({
        injectLegacyMode: schemaFactory.boolean().default(false),
        promptGuidance: schemaFactory.boolean().default(true),
        zhPrompt: schemaFactory.boolean().default(false),
        godotServerDist: schemaFactory.string().default(''),
        godotProjectPath: schemaFactory.string().default(''),
      })
      settings = (ctx.get('settings') as SettingsServiceShape | undefined | null)?.register(
        SETTINGS_NAMESPACE, Config, {
          base: { ...SETTINGS_BASE },
          applies: 'live',
          exposeToClients: true,
        },
      )
    } catch (error) {
      warn(`[${PKG}] settings 注册失败,按默认注入策略工作: ${errorMessage(error)}`)
      settings = undefined
    }
  } else {
    warn(`[${PKG}] schemastery 不可用,设置卡片停用;按默认注入策略工作(仅 Godot preset 注入)`)
  }

  let current: GodotSettings = resolveSettings(settings === undefined ? undefined : settings.get())
  let stopped = false

  // ---- 路径存储(跨会话持久):serverDist 全局单值 + 项目路径按 cwd 批量条目。 ----
  const pathStore = new PathStore()
  /** 按「调用会话 cwd」解析生效路径:serverDist 全局单值,projectPath 按 cwd 查表,
   *  无匹配时若 paths.json 只有**唯一**项目条目则以其兜底(常见:工作台 cwd 非 Godot
   *  项目目录但全机只配了一个项目),否则回退全局设置。 */
  function resolvePathsFor(cwd: string | undefined): { godotServerDist: string; godotProjectPath: string } {
    const projects = pathStore.getConfig().projects
    const sole = projects.length === 1 ? projects[0]!.projectPath : undefined
    const projectPath = pathStore.getProjectPath(cwd)
      ?? sole
      ?? current.godotProjectPath
    return {
      godotServerDist: pathStore.getServerDist() ?? current.godotServerDist,
      // 设置卡/手填可能带 project.godot 文件后缀:统一归一化为项目目录(注册表键形状)。
      godotProjectPath: projectPathToDir(projectPath),
    }
  }

  // ---- Godot 桥接管理器(懒启动 + per-cwd 传参;空闲自动关闭)。
  // 工具清单缓存:连接成功后持久化,「打开 DSH 但无需 Godot」时零连接/零开销。 ----
  const tools = ctx.get('tools') as (ToolsServiceShape & { register(definition: Record<string, unknown>): Disposer }) | undefined | null
  const toolsCache = new ToolsCacheStore()
  const bridge = new GodotBridge({
    serverDist: (cwd?: string) => resolvePathsFor(cwd).godotServerDist,
    projectPath: (cwd?: string) => resolvePathsFor(cwd).godotProjectPath,
    onListChanged: () => { try { void resyncFacade() } catch { /* 重注册失败静默,下次再试 */ } },
    onExit: () => { /* 动态模式:工具保留在册(清单已缓存),调用时 ensureStarted 自愈 */ },
    idleCloseMs: 10 * 60_000,
  })

  // ---- 工具注册(动态清单;无离线占位/无写死清单)。 ----
  interface FacadeState { names: string[]; defs: McpToolDef[] }
  let facadeDisposers: Disposer[] = []
  let facadeState: FacadeState | undefined

  function registerFacade(defs: McpToolDef[]): void {
    if (tools === undefined || tools === null) return
    for (const dispose of facadeDisposers.splice(0)) {
      try { dispose() } catch { /* 继续清理其余 */ }
    }
    facadeDisposers = []
    for (const def of defs) {
      const publicName = `${toolPrefix}${def.name}`
      try {
        facadeDisposers.push(tools.register({
          name: publicName,
          description: resolveToolDescription(
            current.zhPrompt,
            UPSTREAM_TOOL_ZH[def.name],
            def.description,
            `Godot tool (${def.name}). The bridge starts on demand; the first call may take a moment.`,
          ),
          parameters: def.inputSchema ?? { type: 'object' },
          output: {
            schema: { type: 'object' },
            render: (_args: unknown, value: unknown) => {
              const content = (value as { content?: unknown } | undefined)?.content
              return Array.isArray(content) ? content : []
            },
          },
          execute: async (rawArgs: unknown, exec?: unknown) => {
            const args = typeof rawArgs === 'object' && rawArgs !== null ? rawArgs : {}
            // 调用会话 cwd(per-cwd 桥接传参键):模型调用经 agent loop,exec.agent 带上会话。
            const cwd = (exec as { agent?: { session?: { header?: { cwd?: string } } } } | undefined)?.agent?.session?.header?.cwd
            const result = await bridge.callTool(def.name, args, cwd)
            // 首次成功调用后(尚未同步过清单)补一次 tools/list → 注册 + 写缓存。
            // 之后不再随调用刷新(有缓存时零往返),server 的 tools/list_changed 负责增量。
            if (bridge.toolDefsCache === undefined) {
              void syncFacadeFromServer().catch(() => { /* 清单同步失败静默:下次调用/连接重试 */ })
            }
            const content = Array.isArray(result.content) ? result.content : []
            // MVP:图片块降级为文本占位(附件投影留待后续迭代);文本原样透传。
            const projected = content.map(block => {
              const record = block as Record<string, unknown>
              return record.type === 'image'
                ? { type: 'text', text: '[image result: attachment projection pending — see workbench for the image]' }
                : block
            })
            const text = projected
              .map(block => (block as { type?: string; text?: string }).text ?? '')
              .filter(part => part !== '')
              .join('\n')
            if (result.isError === true) throw new Error(toolFailureMessage(current.zhPrompt, def.name, text))
            return { content: projected }
          },
        }))
      } catch (error) {
        warn(`[${PKG}] 门面工具注册失败(${publicName}): ${errorMessage(error)}`)
      }
    }
    facadeDisposers = facadeDisposers.filter(dispose => typeof dispose === 'function')
  }

  /** 以 server 清单重注册门面。清单与在册一致时跳过(防 re-sync 风暴);zhPrompt 变化强制。 */
  function applyDefs(defs: McpToolDef[], force = false): void {
    if (stopped) return
    const next = defs.map(def => def.name)
    const state = facadeState
    if (!force && state !== undefined
      && next.length === state.names.length
      && next.every((n, i) => n === state.names[i])) {
      return
    }
    registerFacade(defs)
    facadeState = { names: next, defs }
  }

  /** 在线精确:tools/list 的真实 schema/description 注册全部工具(动态清单),成功后写缓存。 */
  async function syncFacadeFromServer(): Promise<void> {
    const defs: McpToolDef[] = await bridge.listTools()
    if (stopped) return
    applyDefs(defs)
    toolsCache.save({
      serverDist: resolvePathsFor(undefined).godotServerDist || undefined,
      tools: defs.map(def => ({ name: def.name, description: def.description, inputSchema: def.inputSchema })),
    })
  }

  /** server 通知 list_changed(组激活/扩展/重连)时重同步。 */
  async function resyncFacade(): Promise<void> {
    try {
      await syncFacadeFromServer()
    } catch (error) {
      warn(`[${PKG}] 工具面重同步失败(保留在册清单): ${errorMessage(error)}`)
    }
  }

  /**
   * 从持久缓存加载工具清单(不 spawn server、不连编辑器):
   * 打开的 DSH 不一定要做 Godot 相关操作——有缓存时工具直接可见、零连接开销;
   * 首次工具调用或工作台「连接 Godot」才拉起桥接(连接成功后刷新缓存)。
   * 缓存只接受与**当前 server dist** 匹配的快照(同一 server 的工具面一致;
   * 项目差异由调用时的 per-cwd 传参承担),不匹配视为陈旧,忽略待刷新。
   */
  function loadFacadeFromCache(): void {
    if (tools === undefined || tools === null) return
    try {
      const dist = resolvePathsFor(undefined).godotServerDist
      const cached = toolsCache.get()
      if (cached === undefined || cached.tools.length === 0) return
      if (dist === '' || cached.serverDist !== dist) {
        warn(`[${PKG}] 工具清单缓存与当前 server dist 不匹配,忽略缓存(在工作台点「连接 Godot」刷新)`)
        return
      }
      applyDefs(cached.tools.map(tool => ({ name: tool.name, description: tool.description, inputSchema: tool.inputSchema })))
    } catch (error) {
      warn(`[${PKG}] 工具缓存加载失败: ${errorMessage(error)}`)
    }
  }

  /** 工作台「连接 Godot」:spawn server + 同步清单 + 写缓存(编辑器由工具调用时按需连接)。
   *  `cwd` 为工作台当前会话工作目录(per-cwd 路径解析键),由路由 query 传入。 */
  async function connectWorkbench(cwd?: string): Promise<{ ok: boolean; toolCount: number }> {
    bridge.setCwd(cwd)
    await bridge.ensureStarted()
    await syncFacadeFromServer()
    return { ok: true, toolCount: facadeState?.names.length ?? 0 }
  }

  // ---- 工作台(tools 目录缓存 + webServer 延迟装配;与注入策略无关)。 ----
  if (tools !== undefined && tools !== null) {
    const executor = new WorkbenchExecutor()
    const cache = new ToolsCatalogueCache()
    ctx.on('tools/change', () => { cache.invalidate() })
    const mount = (mountCtx: HostContext): void => {
      const disposeRoutes = mountWorkbenchRoutes(mountCtx, {
        tools,
        systemPrompt: promptService,
        cache,
        config: {
          serverName,
          godotMcpRoot,
          projectPath: current.godotProjectPath,
          unsafe,
          serverDistPath: current.godotServerDist,
          pathStore,
          connect: (cwd?: string) => connectWorkbench(cwd),
        },
        executor,
        warn,
      })
      if (disposeRoutes !== undefined) {
        mountCtx.effect(() => disposeRoutes, `${PKG}: workbench routes teardown`)
      }
    }
    if (typeof ctx.inject === 'function') ctx.inject(['webServer'], (hostCtx?: HostContext) => { mount(hostCtx ?? ctx) })
    else mount(ctx)
  }

  // ---- 注入策略:全局 section(仅 legacy)+ per-agent deny/scoped section。 ----
  let globalSectionDisposer: Disposer | undefined
  interface PerAgentSurface { disposers: Disposer[]; denied: string[]; hasPrompt: boolean; hide: boolean }
  const perAgent = new Map<AgentShape, PerAgentSurface>()
  let cachedNames: string[] | undefined
  function invalidateNames(): void { cachedNames = undefined }

  function disposeEntry(entry: PerAgentSurface): void {
    for (const dispose of [...entry.disposers].reverse()) {
      try { dispose() } catch { /* 继续清理其余 */ }
    }
    entry.disposers.length = 0
    entry.denied = []
  }

  function godotNames(): string[] {
    if (cachedNames !== undefined) return cachedNames
    try {
      const service = tools !== undefined && tools !== null ? tools : (ctx.get('tools') as ToolsServiceShape | undefined)
      if (service === undefined || typeof service.schemas !== 'function') { cachedNames = []; return cachedNames }
      cachedNames = godotToolNames(service.schemas(), toolPrefix)
    } catch { cachedNames = [] }
    return cachedNames
  }

  function isGodotAgent(agent: AgentShape): boolean {
    try {
      const presets = ctx.get('agentPresets') as AgentPresetsShape | undefined | null
      if (presets === undefined || presets === null || typeof presets.composedPreset !== 'function') return false
      return presets.composedPreset(agent.ctx) === GODOT_PRESET_ID
    } catch {
      return false
    }
  }

  function scopedToolsOf(agent: AgentShape) {
    return agent?.ctx?.tools ?? (agent?.ctx?.get?.('tools') as import('./types.js').ScopedToolsShape | undefined)
  }

  function scopedPromptOf(agent: AgentShape): SystemPromptService | undefined {
    return (agent?.ctx?.get?.('systemPrompt') as SystemPromptService | undefined)
      ?? (agent?.ctx as unknown as { systemPrompt?: SystemPromptService } | undefined)?.systemPrompt
  }

  function installFor(agent: AgentShape, cachedNames?: string[]): void {
    const names = cachedNames ?? godotNames()
    const presetId = isGodotAgent(agent) ? GODOT_PRESET_ID : undefined
    const hide = shouldHideAgent({ injectLegacyMode: current.injectLegacyMode, presetId })
    const wantsPrompt = needsScopedPrompt({ injectLegacyMode: current.injectLegacyMode, presetId, promptGuidance: current.promptGuidance })

    const existing = perAgent.get(agent)
    if (existing !== undefined && existing.hide === hide && existing.hasPrompt === wantsPrompt
      && existing.denied.length === (hide ? names.length : 0)
      && (hide === false || names.every((n, i) => existing.denied[i] === n))) {
      return
    }
    if (existing !== undefined) disposeEntry(existing)
    const entry: PerAgentSurface = { disposers: [], denied: [], hasPrompt: wantsPrompt, hide }
    perAgent.set(agent, entry)
    const scopedTools = scopedToolsOf(agent)

    if (hide) {
      if (names.length > 0 && scopedTools?.restrict !== undefined) {
        const disposeRestriction = scopedTools.restrict({ deny: names })
        if (typeof disposeRestriction === 'function') entry.disposers.push(disposeRestriction)
        entry.denied = names
      }
    }

    if (wantsPrompt) {
      const scopedPrompt = scopedPromptOf(agent)
      if (scopedPrompt !== undefined && typeof scopedPrompt.section === 'function') {
        entry.disposers.push(scopedPrompt.section({ name: SECTION_NAME, order: SECTION_ORDER, text: () => sectionText(serverName, current.zhPrompt) }))
      }
    }
  }

  function reinstallAll(): void {
    const agents = (ctx.get('agents') as { list?(): AgentShape[] } | undefined | null)?.list?.() ?? []
    if (agents.length === 0) return
    const names = godotNames()
    for (const agent of agents) {
      try { installFor(agent, names) } catch (error) { warn(`[${PKG}] agent 注入策略重装失败: ${errorMessage(error)}`) }
    }
  }

  /**
   * 增量 deny 补丁:门面 re-sync/组激活新增的 godot 工具补进已隐藏 agent 的
   * deny 名单。**合并窗口内只执行一次**——tools/change 事件在注册时逐工具发出,
   * 逐事件处理会重复全量 `schemas()` 克隆并把 host 主线程打满。
   * 幂等:无新增则不调用 restrict(restrict 自身会再触发 tools/change,无 diff
   * 守卫会事件回声)。
   */
  function refreshDenyPatches(): void {
    const names = godotNames()
    if (names.length === 0) return
    for (const [agent, entry] of perAgent) {
      if (!entry.hide) continue
      const missing = names.filter(name => !entry.denied.includes(name))
      if (missing.length === 0) continue
      const scopedTools = scopedToolsOf(agent)
      const disposeRestriction = scopedTools?.restrict?.({ deny: missing })
      if (typeof disposeRestriction === 'function') entry.disposers.push(disposeRestriction)
      entry.denied = entry.denied.concat(missing)
    }
  }

  let denyRefreshTimer: NodeJS.Timeout | undefined
  function scheduleDenyRefresh(): void {
    if (denyRefreshTimer !== undefined) return
    denyRefreshTimer = setTimeout(() => {
      denyRefreshTimer = undefined
      if (stopped) return
      try {
        invalidateNames()
        refreshDenyPatches()
      } catch { /* 补丁失败静默:下一轮 tools/change 会再次尝试 */ }
    }, DENY_REFRESH_DEBOUNCE_MS)
    denyRefreshTimer.unref?.()
  }

  function refreshGlobalSection(): void {
    if (promptService === undefined) return
    if (globalSectionDisposer !== undefined) {
      try { globalSectionDisposer() } catch { /* 继续重建 */ }
      globalSectionDisposer = undefined
    }
    if (!current.injectLegacyMode || !current.promptGuidance) return
    try {
      globalSectionDisposer = promptService.section({ name: SECTION_NAME, order: SECTION_ORDER, text: () => sectionText(serverName, current.zhPrompt) })
    } catch (error: unknown) {
      const message = errorMessage(error)
      if (/duplicate|already/i.test(message)) {
        warn(`[${PKG}] section 已由先到实例注册,本实例不重复注册`)
        globalSectionDisposer = undefined
      } else {
        warn(`[${PKG}] 全局提示词 section 注册失败: ${message}`)
      }
    }
  }

  function refresh(): void {
    refreshGlobalSection()
    reinstallAll()
  }

  ctx.effect(
    () => () => {
      stopped = true
      if (denyRefreshTimer !== undefined) { clearTimeout(denyRefreshTimer); denyRefreshTimer = undefined }
      for (const entry of perAgent.values()) disposeEntry(entry)
      perAgent.clear()
      if (globalSectionDisposer !== undefined) {
        try { globalSectionDisposer() } catch { /* teardown 尽力而为 */ }
        globalSectionDisposer = undefined
      }
      for (const dispose of facadeDisposers.splice(0)) {
        try { dispose() } catch { /* teardown 尽力而为 */ }
      }
      facadeState = undefined
      bridge.stop()
    },
    `${PKG}: injection policy teardown`,
  )

  if (settings !== undefined) {
    ctx.effect(
      () => settings.watch((next) => {
        if (stopped) return
        const previous = current
        current = resolveSettings(next)
        refresh()
        // 中文化开关变化:工具描述随注册定型,需重注册门面(翻译映射,不清单)。
        if (previous.zhPrompt !== current.zhPrompt && facadeState !== undefined) {
          applyDefs(facadeState.defs, true)
        }
      }),
      `${PKG}: settings watch`,
    )
  }

  ctx.on('agent/created', (payload: unknown) => {
    try {
      if (stopped) return
      const { agent } = payload as { agent?: AgentShape }
      if (agent === undefined) return
      installFor(agent)
      // Godot preset 会话:从持久缓存加载工具清单(零连接);连接由首次调用/工作台触发。
      if (isGodotAgent(agent)) loadFacadeFromCache()
    } catch (error) {
      warn(`[${PKG}] agent/created 注入策略装配失败: ${errorMessage(error)}`)
    }
  })
  ctx.on('agent/disposed', (payload: unknown) => {
    const { agent } = payload as { agent?: AgentShape }
    if (agent !== undefined) perAgent.delete(agent)
  })
  ctx.on('agent-preset/selected', (sessionId: unknown) => {
    try {
      if (stopped) return
      const list = (ctx.get('agents') as { list?(): AgentShape[] } | undefined | null)?.list?.() ?? []
      const agent = list.find(candidate =>
        candidate?.session?.header?.id === sessionId || candidate?.session?.id === sessionId || candidate?.id === sessionId)
      if (agent !== undefined) {
        installFor(agent)
        if (isGodotAgent(agent)) loadFacadeFromCache()
      }
    } catch (error) {
      warn(`[${PKG}] agent-preset/selected 注入策略重装失败: ${errorMessage(error)}`)
    }
  })
  ctx.on('tools/change', () => {
    try {
      if (stopped || current.injectLegacyMode) return
      // 合并窗口内的多事件 → 单次处理(增量 deny)。
      scheduleDenyRefresh()
    } catch {
      // 调度失败静默:下一轮 tools/change 会再次尝试。
    }
  })

  // 初始装配:legacy 全局 section(若开)+ 既有 agent 的 per-agent 表面;
  // 桥接默认不启动(零连接)——工具清单来自持久缓存(loadFacadeFromCache)或
  // 工作台「连接 Godot」;打开 DSH 不一定要做 Godot 相关操作。
  refresh()
}
