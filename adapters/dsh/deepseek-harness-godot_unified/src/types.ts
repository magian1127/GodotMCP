/**
 * Host 侧最小类型面:只声明本插件用到的 Cordis/DSH 形状。
 * 运行时真值以 DSH 实际注入的服务为准;这里保持结构化最小子集。
 */

/** 卸载函数:Cordis 约定的副作用回收句柄。 */
export type Disposer = () => void

/** systemPrompt 服务:section 注册返回 disposer。 */
export interface SystemPromptService {
  section(input: { name: string; order?: number; text: () => string }): Disposer
  assemble(input?: unknown): Promise<{ sections?: Array<{ name: string; text: string }> }>
}

/**
 * `ctx.plugin()` 返回的 Cordis Fiber(本插件用到的子集)。
 *
 * 只需要两个能力:**可 await**(启动完成或失败)与 **dispose()**(卸载)。
 * 刻意不 import `@deepseek-ai/cordis`——本包遵守工作区"不把 DSH 运行时模块写进
 * dependencies"的约定,类型面同样保持鸭子类型。
 */
export interface PluginFiberShape {
  then?(
    onfulfilled?: (() => void) | null,
    onrejected?: ((error: unknown) => void) | null,
  ): unknown
  dispose(): unknown
}

/** Cordis Fiber 上下文(本插件用到的子集)。 */
export interface HostContext {
  get(service: string): unknown
  effect(disposer: () => Disposer | void, label?: string): void
  on(event: string, listener: (payload: unknown) => void): void
  /** 挂载一个 Cordis 插件(默认挂进当前作用域;agent.ctx 上即该会话作用域)。 */
  plugin?(plugin: unknown, config?: unknown): PluginFiberShape
  /** Cordis 延迟注入(webServer 等服务就绪时回调;不可用时回退直接装配)。 */
  inject?(deps: string[], callback: (ctx: HostContext) => void): void
  logger?: { warn(message: string): void; error(message: string): void }
}

/** webServer.register 的最小形状。 */
export interface WebServerServiceShape {
  register(options: { kind?: string; path: string; handler: (req: unknown, res: unknown) => void }): () => void
}

// ---------------------------------------------------------------------------
// 会话维度(agent/created 事件与 agent scope)所需的服务最小形状。
// ---------------------------------------------------------------------------

/** agent scope 上下文:挂载点 + scoped systemPrompt。 */
export interface AgentContextShape {
  plugin?(plugin: unknown, config?: unknown): PluginFiberShape
  systemPrompt?: SystemPromptService
  get?(name: string): unknown
}

/** agent/created 等事件携带的 agent 最小形状。 */
export interface AgentShape {
  id?: string
  ctx?: AgentContextShape
  session?: { header?: { id?: string; cwd?: string }; id?: string }
}

/** agents 服务子集:列出当前全部 agent。 */
export interface AgentsServiceShape {
  list(): AgentShape[]
}

/** dsh-agent-presets 服务子集:读取一个 agent 已加入的 preset id。 */
export interface AgentPresetsShape {
  composedPreset?(agentCtx: unknown): string | undefined
}
