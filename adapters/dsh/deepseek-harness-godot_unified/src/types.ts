/**
 * Host 侧最小类型面:只声明本插件用到的 Cordis/DSH 形状。
 * 运行时真值以 DSH 实际注入的服务为准;这里保持结构化最小子集。
 */

/** 卸载函数:Cordis 约定的副作用回收句柄。 */
export type Disposer = () => void

/** systemPrompt 服务:section 注册返回 disposer;assemble 供工作台展示当前分区。 */
export interface SystemPromptService {
  section(input: { name: string; order?: number; text: () => string }): Disposer
  assemble(input?: unknown): Promise<{ sections?: Array<{ name: string; text: string }> }>
}

/** Cordis Fiber 上下文(本插件用到的子集)。 */
export interface HostContext {
  get(service: string): unknown
  effect(disposer: () => Disposer | void, label?: string): void
  on(event: string, listener: (payload: unknown) => void): void
  /** Cordis 延迟注入(webServer 等服务就绪时回调;不可用时回退直接装配)。 */
  inject?(deps: string[], callback: (ctx: HostContext) => void): void
  logger?: { warn(message: string): void; error(message: string): void }
}

/** 工作台只读消费的工具 schema 子集。 */
export interface ToolSchemaShape {
  name: string
  description?: string
  parameters?: Record<string, unknown>
}

/** 工作台执行结果的最小子集(成功/失败两态)。 */
export interface ToolExecutionResultShape {
  isError: boolean
  value?: unknown
  content?: Array<Record<string, unknown>>
  error?: { message?: string }
}

/** ctx.tools 服务子集(schemas/get/execute)。 */
export interface ToolsServiceShape {
  schemas(): ToolSchemaShape[]
  get(name: string): { name: string; parameters?: Record<string, unknown> } | undefined
  execute(exec: { callId: string; name: string; arguments: unknown; signal: AbortSignal }): Promise<ToolExecutionResultShape>
}

/** webServer.register 的最小形状。 */
export interface WebServerServiceShape {
  register(options: { kind?: string; path: string; handler: (req: unknown, res: unknown) => void }): () => void
}

// ---------------------------------------------------------------------------
// 注入策略(settings 卡片 + per-agent 表面)所需的服务最小形状。
// 运行时真值以 DSH 实际注入的服务为准;全部结构化最小子集。
// ---------------------------------------------------------------------------

/** settings 服务命名空间注册(live 应用 + 客户端暴露),对齐 hashline 模式。 */
export interface SettingsRegistrationShape {
  get(): Record<string, unknown>
  watch(listener: (next: Record<string, unknown>) => void): Disposer
}

export interface SettingsServiceShape {
  register(
    namespace: string,
    schema: unknown,
    options: { base: Record<string, unknown>; applies: 'live'; exposeToClients: boolean },
  ): SettingsRegistrationShape
}

/** agent/created 等事件携带的 agent 最小形状。 */
export interface AgentShape {
  id?: string
  ctx?: {
    tools?: ScopedToolsShape
    systemPrompt?: SystemPromptService
    get?(name: string): unknown
  }
  session?: { header?: { id?: string }; id?: string }
}

/** agents 服务子集:列出当前全部 agent。 */
export interface AgentsServiceShape {
  list(): AgentShape[]
}

/** agent scope 的 tools 子集:restrict 仅在 scoped context 可用。 */
export interface ScopedToolsShape {
  restrict?(filter: { deny?: string[] }): Disposer | undefined
}

/** dsh-agent-presets 服务子集:读取一个 agent 已加入的 preset id。 */
export interface AgentPresetsShape {
  composedPreset?(agentCtx: unknown): string | undefined
}
