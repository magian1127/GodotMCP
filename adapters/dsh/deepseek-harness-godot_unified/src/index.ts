/**
 * deepseek-harness-godot_unified —— host 半边入口(v0.5:官方 MCP)。
 *
 * 本包**不实现 MCP 客户端**:连接、握手、工具发现、`tools/list_changed`、断线重连、
 * 图片投影全部交给宿主自带的 `@deepseek-ai/dsh-mcp-client`(运行时从 profile 解析,
 * 不写进 dependencies)。本插件只回答两个问题:**哪些会话需要它**、**它连哪个 server**。
 *
 * 1. **按会话挂载**(默认):只有加入 Godot preset 的会话,在 `agent/created` 时把
 *    mcp-client 挂进该 agent 的作用域。其他会话里**根本没有**这些工具——不是
 *    "注册后 deny",因此不再需要 restrict / tools/change 合并窗口 / 工具清单缓存。
 *    工具名由官方约定给出:`mcp__<serverName>__<rawName>`。
 * 2. **按会话传项目路径**:挂载时按**该会话的 cwd** 解析 Godot 项目路径,作为
 *    `GODOT_MCP_PROJECT_PATH` 传给 server;会话 cwd 同时作为子进程工作目录
 *    (shim 未拿到显式 env 时会按 cwd 查机器级注册表兜底)。同一台机器上不同会话
 *    可以是不同 Godot 项目。
 * 3. **原版模式**(`injectLegacyMode`):在 host 作用域挂一个**共享**连接,所有会话
 *    可见;项目路径取全局设置(不再按会话切换)。该模式下不再按会话挂载,避免
 *    同一份工具挂两遍、多起一个进程。
 * 4. **不阻塞会话创建**:挂载本身不 await 连接(官方客户端自带重连),只给 Godot
 *    会话一个上限为 `READY_GRACE_MS` 的宽限,让首条消息通常就能看到工具;
 *    失败一律降级为日志,绝不向会话创建路径抛错。
 * 5. **路径设置 HTTP 路由**(`/godot-config/api/path-settings`)不变。
 *
 * 配置走插件行(`dsh-godot` 行 config):注入两开关 + 提示词中文化 + server dist/
 * 项目路径参考值;per-cwd 路径经 `$DSH_HOME/godot/paths.json`(插件页配置表单可维护)。
 */
import { PKG, SECTION_NAME, SECTION_ORDER } from './constants.js'
import { resolveSettings, isGodotPreset, shouldInjectPrompt, type GodotSettings } from './injection.js'
import { buildMountConfig, errorName, mountConfigKey, type GodotMcpConfig } from './mcp-mount.js'
import { mountPathSettingsRoute } from './path-settings-route.js'
import { PathStore } from './path-store.js'
import { loadMcpClient, loadSchemastery } from './profile-modules.js'
import { notConfiguredSectionText, sectionText } from './section-text.js'
import type {
  AgentPresetsShape,
  AgentShape,
  Disposer,
  HostContext,
  PluginFiberShape,
  SystemPromptService,
} from './types.js'

export { GODOT_PRESET_ID } from './constants.js'

export const name = PKG

/**
 * 同步依赖的宿主服务。
 *
 * `tools` 不在列表里:mcp-client 自己声明 `inject: ['tools']`,Cordis 会在它那一层
 * 等工具服务就绪;本插件不再注册任何工具。
 */
export const inject = ['systemPrompt', 'agents']

/** 插件 Config 的 schemastery 工厂形状(仅覆盖本插件用到的链式 API)。 */
interface GodotSchemaField {
  default(value: unknown): GodotSchemaField
  /** DSH 0.1.7+ schemastery(3.18.3)提供易变标记;3.18.2 缺席时走 meta 直写。 */
  volatile?(): GodotSchemaField
  meta?: Record<string, unknown>
}

interface GodotSchemaFactory {
  object(shape: Record<string, unknown>): unknown
  boolean(): GodotSchemaField
  string(): GodotSchemaField
  /** 数值字段（schemastery 的 `number()`）；旧版没有该成员，调用点用可选兜底。 */
  number?(): GodotSchemaField
}

function markVolatile(field: GodotSchemaField): GodotSchemaField {
  if (field === null || field === undefined) return field
  if (typeof field.volatile === 'function') return field.volatile()
  // 旧 schemastery(3.18.2 无 .volatile()):直接打 meta.volatile——宿主
  // volatileForm/diff 读的就是 meta。schema 实例是 function 类型,不能用
  // typeof 'object' 守卫;旧版 resolve 不物化引用,变更按「重挂载生效」。
  field.meta = { ...(field.meta ?? {}), volatile: true }
  return field
}

/**
 * 宿主(DSH 0.1.7+)读取的插件 Config。
 *
 * 只有 `serverName` 是普通字段(改它会让工具命名空间变化,宿主按重挂载处理);
 * 其余全 volatile:插件页实时编辑,值持久化在 profile 行 config,变更经
 * loader/volatile-update 派发。schemastery 不可用时导出 undefined(不能是
 * null:宿主 settings 的 `'toJSON' in Config` 检查遇 null 会崩掉整页设置),
 * 宿主跳过 schema,config 以普通值传入(按默认注入策略工作)。
 *
 * 刻意不再声明 v0.4 的 `godotMcpRoot` / `projectPath` / `unsafe`:它们在 v0.5 没有
 * 任何消费方(server 入口来自 paths.json 或 `godotServerDist`,项目路径来自
 * `godotProjectPath`/按 cwd 条目),留着只会让用户在行 config 里填了却不生效。
 */
const configSchemaFactory = loadSchemastery() as GodotSchemaFactory | null
export const Config =
  configSchemaFactory === null || configSchemaFactory === undefined
    ? undefined
    : configSchemaFactory.object({
        serverName: configSchemaFactory.string().default('godot'),
        injectLegacyMode: markVolatile(configSchemaFactory.boolean().default(false)),
        promptGuidance: markVolatile(configSchemaFactory.boolean().default(true)),
        zhPrompt: markVolatile(configSchemaFactory.boolean().default(false)),
        godotServerDist: markVolatile(configSchemaFactory.string().default('')),
        godotProjectPath: markVolatile(configSchemaFactory.string().default('')),
        // 宿主层预算(毫秒)。必须 > shim 的 210s:否则慢调用会先被宿主掐断,
        // 拿不到 daemon 给出的清晰超时说明。改它会触发重挂(mountConfigKey 含它)。
        // 旧版 schemastery 没有 number():退化成字符串字段,resolveSettings 两者都接受。
        toolCallTimeoutMs: markVolatile(
          typeof configSchemaFactory.number === 'function'
            ? configSchemaFactory.number().default(240_000)
            : configSchemaFactory.string().default('240000'),
        ),
      })

/**
 * 组合行 config 解析:只保留真正被消费的 `serverName`(MCP 命名空间)。
 *
 * v0.4 的 `godotMcpRoot` / `projectPath` / `unsafe` 已删除——host 侧从未消费它们,
 * 保留解析只会制造"填了却无效"的假象。
 */
export function parsePluginConfig(config: Record<string, unknown> = {}): { serverName: string } {
  const str = (v: unknown): string | undefined => (typeof v === 'string' && v.trim().length > 0 ? v.trim() : undefined)
  return { serverName: str(config.serverName) ?? 'godot' }
}

/**
 * Godot 会话创建时的就绪宽限(ms)。
 *
 * 官方 mcp-client 的 `apply` 会 await 首次连接 + 工具发现;`agent/created` 是串行
 * 且被 await 的事件,因此"等它"能让第一条用户消息就看到工具。但握手可能拖到
 * SDK 的 60s 超时,所以只给一个有上限的宽限:连接更快就继续,超过上限就让
 * 工具在后台就位(`tools/change` 会让模型在下一轮看到它们)。
 *
 * 取 10s:冷启动 daemon + editor 未开时的静默待命通常都在这个量级内,而会话创建
 * 卡 10s 已是可接受的上限。
 */
const READY_GRACE_MS = 10_000

/** 一个 agent 在插件侧的挂载记账。 */
interface AgentMount {
  /** 记账键:`agent.id`(SessionId),**不是** agent 对象引用。 */
  key: string
  /** 配置身份键(dist + 项目路径 + cwd);变化即重挂。 */
  configKey: string
  /** 挂载成功时的 fiber;未配置/失败时为 undefined。 */
  fiber?: PluginFiberShape
  /** scoped 提示词 section 的 disposer。 */
  prompt?: Disposer
  /** 当前是否处于"没有可挂载配置"状态(提示词走 notConfigured 文本)。 */
  unconfigured: boolean
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

/** 等一个 fiber 就绪,但最多等 `ms`;永不抛错。 */
async function withReadyGrace(work: PromiseLike<unknown>, ms: number = READY_GRACE_MS): Promise<void> {
  let timer: NodeJS.Timeout | undefined
  const timeout = new Promise<void>((resolve) => {
    timer = setTimeout(resolve, ms)
    timer.unref?.()
  })
  try {
    await Promise.race([Promise.resolve(work).then(() => undefined, () => undefined), timeout])
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}

export function apply(ctx: HostContext, config: Record<string, unknown> = {}): void {
  // 宿主物化的 volatile 引用(带 .get())读成普通值快照;普通字段原样保留
  // (Config 为 null、宿主跳过 schema 时的降级路径)。行配置(serverName 等)
  // 与设置字段统一从该快照消费。
  const readSettingsSnapshot = (): Record<string, unknown> => {
    const snapshot: Record<string, unknown> = {}
    for (const [key, value] of Object.entries(config)) {
      snapshot[key] = value !== null && typeof value === 'object' && typeof (value as { get?: unknown }).get === 'function'
        ? (value as { get(): unknown }).get()
        : value
    }
    return snapshot
  }
  const { serverName } = parsePluginConfig(readSettingsSnapshot())
  const warn = (message: string): void => { try { ctx.logger?.warn(message) } catch { /* 日志不可用不致命 */ } }

  const systemPrompt = ctx.get('systemPrompt') as SystemPromptService | undefined | null
  const promptService: SystemPromptService | undefined = systemPrompt ?? undefined

  // 宿主自带的 MCP 客户端(插件本体)。拿不到就整体降级为"不挂载"——绝不自建一份。
  const mcpClient = loadMcpClient()
  if (mcpClient === null || mcpClient === undefined) {
    warn(`[${PKG}] 未能从 profile 解析到 @deepseek-ai/dsh-mcp-client:Godot MCP 不会挂载,工具不会出现`)
  }

  let current: GodotSettings = resolveSettings(readSettingsSnapshot())
  let stopped = false

  // ---- 路径存储:只保留 serverDist(服务器安装位置)。 ----
  //
  // 项目路径表已经删掉:一个工作区常有多个 Godot 项目,而查表只能给一个;
  // 会话要连别的项目还得先去配置页改表。现在目标由 daemon 侧解析
  // (godot_connect 的 project 参数 → 本会话默认目标 → shim 上报的 x-godot-cwd
  // 下的 project.godot → 唯一活跃实例),插件只负责告诉服务器 cwd。
  const pathStore = new PathStore()
  /**
   * 解析生效的服务器安装路径(全局单值,与项目路径无关)。
   *
   * 参数 `cwd` 保留只为兼容既有调用点 —— 项目路径不再按 cwd 查表。
   */
  function resolvePathsFor(_cwd: string | undefined): { godotServerDist: string; godotProjectPath: string } {
    return {
      godotServerDist: pathStore.getServerDist() ?? current.godotServerDist,
      // 保留字段以兼容既有调用点;恒为空串 —— 目标不再由插件决定。
      godotProjectPath: '',
    }
  }

  /** 在把 config 交给 Cordis 之前先过一遍官方 schema。
   *
   * 为什么:Config 校验失败时 schemastery 的 union 错误会把**整个 config**
   * (含 env 值)写进 Cordis 自己的日志;先自校验就能把这类输入挡在外面,
   * 诊断只记错误类型名(与 project-mcp 同款口径)。 */
  function validateMountConfig(configValue: GodotMcpConfig): boolean {
    const factory = (mcpClient as { Config?: unknown } | null | undefined)?.Config
    if (typeof factory !== 'function') return true
    try {
      (factory as (input: unknown) => unknown)(configValue)
      return true
    } catch (error) {
      warn(`[${PKG}] Godot MCP 配置未通过官方 schema 校验,已跳过挂载(错误类型: ${errorName(error)})`)
      return false
    }
  }

  // ---- agent 维度记账(键是会话 id;见 AgentMount) ----
  const mounts = new Map<string, AgentMount>()

  function agentKeyOf(agent: AgentShape): string | undefined {
    const id: unknown = agent.id ?? agent.session?.header?.id ?? agent.session?.id
    return typeof id === 'string' && id !== '' ? id : undefined
  }

  function presetOf(agent: AgentShape): string | undefined {
    try {
      const presets = ctx.get('agentPresets') as AgentPresetsShape | undefined | null
      if (presets === undefined || presets === null || typeof presets.composedPreset !== 'function') return undefined
      return presets.composedPreset(agent.ctx)
    } catch {
      return undefined
    }
  }

  function scopedPromptOf(agent: AgentShape): SystemPromptService | undefined {
    const fromGet = agent.ctx?.get?.('systemPrompt')
    if (fromGet !== undefined && fromGet !== null) return fromGet as SystemPromptService
    return agent.ctx?.systemPrompt
  }

  function disposeFiber(label: string, fiber: PluginFiberShape | undefined): void {
    if (fiber === undefined) return
    try {
      void Promise.resolve(fiber.dispose()).catch((error: unknown) => {
        warn(`[${PKG}] 卸载 ${label} 失败(错误类型: ${errorName(error)})`)
      })
    } catch (error) {
      warn(`[${PKG}] 卸载 ${label} 失败(错误类型: ${errorName(error)})`)
    }
  }

  function disposePrompt(entry: AgentMount): void {
    if (entry.prompt === undefined) return
    try { entry.prompt() } catch { /* teardown 尽力而为 */ }
    entry.prompt = undefined
  }

  function disposeMount(entry: AgentMount): void {
    disposePrompt(entry)
    disposeFiber(`会话 ${entry.key} 的 Godot MCP`, entry.fiber)
    entry.fiber = undefined
  }

  /** 该 agent 的提示词 section 与设置对齐(文本函数动态读取 current,无需重注册)。 */
  function syncPrompt(agent: AgentShape, entry: AgentMount, presetId: string | undefined): void {
    const scoped = scopedPromptOf(agent)
    const wanted = shouldInjectPrompt({
      injectLegacyMode: current.injectLegacyMode,
      presetId,
      promptGuidance: current.promptGuidance,
    }) && scoped !== undefined && typeof scoped.section === 'function'
    if (!wanted) {
      disposePrompt(entry)
      return
    }
    if (entry.prompt !== undefined) return
    try {
      entry.prompt = scoped!.section({
        name: SECTION_NAME,
        order: SECTION_ORDER,
        text: () => (entry.unconfigured
          ? notConfiguredSectionText(serverName, current.zhPrompt)
          : sectionText(serverName, current.zhPrompt)),
      })
    } catch (error) {
      const message = errorMessage(error)
      if (/duplicate|already/i.test(message)) {
        warn(`[${PKG}] section 已由先到实例注册,本实例不重复注册`)
      } else {
        warn(`[${PKG}] 提示词 section 注册失败: ${message}`)
      }
    }
  }

  /**
   * 让某个 agent 的 Godot MCP 状态与当前设置/preset 对齐(幂等)。
   *
   * 三条退出路径:不需要(非 Godot preset / 原版模式 / mcp-client 不可用)→ 拆;
   * 已挂且配置键一致 → 只同步提示词;否则重挂。
   */
  async function syncAgent(agent: AgentShape, options: { grace?: boolean } = {}): Promise<void> {
    try {
      if (stopped) return
      const key = agentKeyOf(agent)
      if (key === undefined) {
        // 会话身份缺失属于契约漂移:宁可少挂一次(用户重开会话即恢复),也不能
        // 退回对象引用——同一 sessionId 的第二个 agent 对象会因此重复挂载。
        warn(`[${PKG}] agent 缺少会话 id,无法按会话记账,跳过 Godot MCP 挂载`)
        return
      }

      const existing = mounts.get(key)
      const presetId = presetOf(agent)
      const needed = isGodotPreset(presetId) && !current.injectLegacyMode
        && mcpClient !== null && mcpClient !== undefined

      if (!needed) {
        if (existing !== undefined) {
          mounts.delete(key)
          disposeMount(existing)
        }
        return
      }

      const cwd = agent.session?.header?.cwd
      const paths = resolvePathsFor(cwd)
      const configKey = [paths.godotServerDist, paths.godotProjectPath, cwd ?? ''].join('\u0000')

      const sameConfig = existing !== undefined && existing.configKey === configKey
      const alive = existing !== undefined && (existing.unconfigured || existing.fiber !== undefined)
      if (sameConfig && alive) {
        syncPrompt(agent, existing!, presetId)
        return
      }
      if (existing !== undefined) {
        mounts.delete(key)
        disposeMount(existing)
      }

      // server 入口未配置:不挂载,但给 Godot 会话一段"如何配置"的提示词——
      // 否则模型会凭空调用不存在的工具。配置保存后经 loader/volatile-update 重挂。
      if (paths.godotServerDist === '') {
        const entry: AgentMount = { key, configKey, unconfigured: true }
        mounts.set(key, entry)
        syncPrompt(agent, entry, presetId)
        warn(`[${PKG}] Godot MCP 未挂载:server 入口路径为空(在插件页 → 本包页面 → 「Godot 路径」填写)`)
        return
      }

      const agentCtx = agent.ctx
      if (agentCtx === undefined || typeof agentCtx.plugin !== 'function') return

      let mountConfig: GodotMcpConfig
      try {
        mountConfig = buildMountConfig({
          serverName,
          dist: paths.godotServerDist,
          projectPath: paths.godotProjectPath,
          cwd,
          // 宿主层预算(240s):三层阶梯的最高层,必须大于 shim 的 210s。
          toolCallTimeoutMs: current.toolCallTimeoutMs,
        })
      } catch (error) {
        warn(`[${PKG}] Godot MCP 配置组装失败,跳过挂载(错误类型: ${errorName(error)})`)
        return
      }
      if (!validateMountConfig(mountConfig)) return

      let fiber: PluginFiberShape
      try {
        fiber = agentCtx.plugin(mcpClient, mountConfig)
      } catch (error) {
        warn(`[${PKG}] 挂载 Godot MCP 失败,本会话没有工具(错误类型: ${errorName(error)})`)
        return
      }
      const entry: AgentMount = { key, configKey, fiber, unconfigured: false }
      mounts.set(key, entry)
      syncPrompt(agent, entry, presetId)

      // 只观察启动结果,不阻塞:失败时把 fiber 摘掉,让下一次设置变更/预设切换
      // 触发重挂(官方客户端自身仍会按 reconnect 策略重试,这里只处理它彻底失败)。
      Promise.resolve(fiber as unknown as PromiseLike<unknown>).then(undefined, (error: unknown) => {
        warn(`[${PKG}] Godot MCP 启动失败(会话 ${key},错误类型: ${errorName(error)})`)
        const live = mounts.get(key)
        // 按身份摘除:同名重挂的新 fiber 不得被迟到的旧拒绝抹掉。
        if (live !== undefined && live.fiber === fiber) live.fiber = undefined
      })

      if (options.grace === true) await withReadyGrace(fiber as unknown as PromiseLike<unknown>)
    } catch (error) {
      // 会话创建路径绝不抛错(抛错会打断会话创建)。
      warn(`[${PKG}] Godot MCP 挂载流程异常(错误类型: ${errorName(error)})`)
    }
  }

  // ---- 原版模式:host 作用域的共享连接 + 全局提示词 section ----
  let globalSectionDisposer: Disposer | undefined
  let rootMount: { configKey: string; fiber: PluginFiberShape } | undefined

  function syncGlobalSection(): void {
    // 全局 section 只在原版模式下注册(preset 维度由 scoped section 承担)。
    const wanted = shouldInjectPrompt({
      injectLegacyMode: current.injectLegacyMode,
      promptGuidance: current.promptGuidance,
    }) && promptService !== undefined
    if (!wanted) {
      if (globalSectionDisposer !== undefined) {
        try { globalSectionDisposer() } catch { /* teardown 尽力而为 */ }
        globalSectionDisposer = undefined
      }
      return
    }
    if (globalSectionDisposer !== undefined) return
    // 原版模式同样按"有没有可挂载的 server 入口"切换文本:没有工具时若还教
    // 模型用 `mcp__godot__*`,它就只会臆造调用(与按会话分支同一口径)。
    // 文本函数每次组装时读取当前路径,配置好后自动切回完整工作流。
    try {
      globalSectionDisposer = promptService!.section({
        name: SECTION_NAME,
        order: SECTION_ORDER,
        text: () => (resolvePathsFor(undefined).godotServerDist === ''
          ? notConfiguredSectionText(serverName, current.zhPrompt)
          : sectionText(serverName, current.zhPrompt)),
      })
    } catch (error) {
      const message = errorMessage(error)
      if (/duplicate|already/i.test(message)) {
        warn(`[${PKG}] section 已由先到实例注册,本实例不重复注册`)
      } else {
        warn(`[${PKG}] 全局提示词 section 注册失败: ${message}`)
      }
      globalSectionDisposer = undefined
    }
  }

  function disposeRootMount(): void {
    if (rootMount === undefined) return
    const fiber = rootMount.fiber
    rootMount = undefined
    disposeFiber('原版模式的 Godot MCP', fiber)
  }

  /** 原版模式的共享连接(一个进程/一条连接服务所有会话)。 */
  function syncRoot(): void {
    syncGlobalSection()
    if (!current.injectLegacyMode || mcpClient === null || mcpClient === undefined) {
      disposeRootMount()
      return
    }
    const paths = resolvePathsFor(undefined)
    if (paths.godotServerDist === '') {
      disposeRootMount()
      warn(`[${PKG}] 原版模式已开启,但 server 入口路径为空:Godot MCP 不会挂载`)
      return
    }
    let mountConfig: GodotMcpConfig
    try {
      mountConfig = buildMountConfig({
        serverName,
        dist: paths.godotServerDist,
        projectPath: paths.godotProjectPath,
        // 原版模式没有"当前会话":用宿主进程工作目录作子进程 cwd。
        cwd: process.cwd(),
        toolCallTimeoutMs: current.toolCallTimeoutMs,
      })
    } catch (error) {
      disposeRootMount()
      warn(`[${PKG}] 原版模式 Godot MCP 配置组装失败(错误类型: ${errorName(error)})`)
      return
    }
    const configKey = mountConfigKey(mountConfig)
    if (rootMount !== undefined && rootMount.configKey === configKey) return
    disposeRootMount()
    if (!validateMountConfig(mountConfig)) return
    const plugin = ctx.plugin
    if (typeof plugin !== 'function') {
      warn(`[${PKG}] host ctx 不支持 plugin():原版模式无法挂载 Godot MCP`)
      return
    }
    try {
      rootMount = { configKey, fiber: plugin.call(ctx, mcpClient, mountConfig) }
    } catch (error) {
      warn(`[${PKG}] 原版模式挂载 Godot MCP 失败(错误类型: ${errorName(error)})`)
    }
  }

  function listAgents(): AgentShape[] {
    try {
      const service = ctx.get('agents') as { list?(): AgentShape[] } | undefined | null
      return service?.list?.() ?? []
    } catch {
      return []
    }
  }

  /** 设置或预设变化后的全量对齐(同步部分;每个 agent 的挂载各自异步进行)。 */
  function resyncAll(): void {
    if (stopped) return
    syncRoot()
    for (const agent of listAgents()) {
      try { void syncAgent(agent) } catch { /* syncAgent 内部已兜底 */ }
    }
  }

  // ---- 路径设置路由(webServer 延迟装配;与挂载策略无关)。 ----
  {
    const mount = (mountCtx: HostContext): void => {
      const disposeRoute = mountPathSettingsRoute(mountCtx, {
        pathStore,
        // 传 **getter** 而不是值:按值传入会在挂载那一刻钉死 config 快照,
        // `current` 之后被 volatile 更新重赋值也不会反映到这里。
        serverDistPath: () => current.godotServerDist,
        projectPath: current.godotProjectPath,
        warn,
      })
      if (disposeRoute !== undefined) {
        mountCtx.effect(() => disposeRoute, `${PKG}: path settings route teardown`)
      }
    }
    if (typeof ctx.inject === 'function') ctx.inject(['webServer'], (hostCtx?: HostContext) => { mount(hostCtx ?? ctx) })
    else mount(ctx)
  }

  // ---- 生命周期:卸载时回收全部挂载与 section。 ----
  ctx.effect(
    () => () => {
      stopped = true
      for (const entry of mounts.values()) disposeMount(entry)
      mounts.clear()
      disposeRootMount()
      if (globalSectionDisposer !== undefined) {
        try { globalSectionDisposer() } catch { /* teardown 尽力而为 */ }
        globalSectionDisposer = undefined
      }
    },
    `${PKG}: mcp mounts teardown`,
  )

  // 实时设置:宿主把 volatile 变更以引用提交进 config 并派发
  // loader/volatile-update(仅本 fiber);Config 为 null 时事件不派发,按初始
  // 快照工作。路径/开关变化 → 重挂;zhPrompt 只影响文本函数(闭包读取 current)。
  ctx.on('loader/volatile-update', () => {
    if (stopped) return
    current = resolveSettings(readSettingsSnapshot())
    resyncAll()
  })

  // 会话创建:Godot preset 会话在此挂载。返回 promise 让 Cordis 的串行派发等待
  // 一个有上限的就绪宽限(见 READY_GRACE_MS);syncAgent 不抛错。
  ctx.on('agent/created', (payload: unknown) => {
    if (stopped) return undefined
    try {
      const agent = (payload as { agent?: AgentShape } | undefined)?.agent
      if (agent === undefined) return undefined
      return syncAgent(agent, { grace: true })
    } catch (error) {
      warn(`[${PKG}] agent/created 挂载流程失败(错误类型: ${errorName(error)})`)
      return undefined
    }
  })

  ctx.on('agent/disposed', (payload: unknown) => {
    try {
      const agent = (payload as { agent?: AgentShape } | undefined)?.agent
      if (agent === undefined) return
      const key = agentKeyOf(agent)
      if (key === undefined) return
      const entry = mounts.get(key)
      if (entry === undefined) return
      mounts.delete(key)
      disposeMount(entry)
    } catch (error) {
      warn(`[${PKG}] 回收已销毁会话的挂载失败(错误类型: ${errorName(error)})`)
    }
  })

  ctx.on('agent-preset/selected', (sessionId: unknown) => {
    if (stopped) return
    try {
      const agent = listAgents().find(candidate =>
        candidate?.session?.header?.id === sessionId || candidate?.session?.id === sessionId || candidate?.id === sessionId)
      if (agent === undefined) return
      // 空白会话换预设:换到 Godot 要挂,换走要拆(首轮之后 DSH 会拒绝换预设)。
      // 这里不必等就绪宽限——用户的消息不会被这次切换阻塞。
      void syncAgent(agent)
    } catch (error) {
      warn(`[${PKG}] agent-preset/selected 重装失败(错误类型: ${errorName(error)})`)
    }
  })

  // 初始装配:原版模式的共享连接 + 既有 agent 的会话挂载。
  // 默认不启动连接(godotServerDist 为空时只注入"未配置"提示词);打开 DSH
  // 不一定要做 Godot 相关操作。
  resyncAll()
}
