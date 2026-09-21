/**
 * 注入策略纯函数(node --test 可直接验证;host apply 与设置卡片共用语义)。
 *
 * 注入模型(对齐 deepseek-harness-hashline 的 per-agent 表面模式):
 * - `injectLegacyMode`(注入原版模式,默认关):开启后所有会话全局注入
 *   `godot_*` 工具与工作流提示词(即 bundle 全局挂载的原版行为)。
 * - 默认(关):只有加入 Godot preset 的会话注入(工具可见 + 提示词 section),
 *   其余会话在 Agent 自身作用域 deny 这些工具,且不出现提示词。
 */

/** 注入策略设置(设置卡片 `godot` 命名空间;host 与 client 共用默认值语义)。 */
export interface GodotSettings {
  injectLegacyMode: boolean
  promptGuidance: boolean
  zhPrompt: boolean
  /** GodotMCP server dist 完整路径(空=未配置,工具调用报可行动错误)。 */
  godotServerDist: string
  /** Godot 项目路径(server env GODOT_MCP_PROJECT_PATH)。 */
  godotProjectPath: string
}

export const SETTINGS_DEFAULTS: GodotSettings = {
  injectLegacyMode: false,
  promptGuidance: true,
  zhPrompt: false,
  godotServerDist: '',
  godotProjectPath: '',
}

export const SETTINGS_KEYS = ['injectLegacyMode', 'promptGuidance', 'zhPrompt', 'godotServerDist', 'godotProjectPath'] as const

/** 逐键校验;布尔回退默认,字符串保留(trim 由调用方处理),非法形状回退默认。 */
export function resolveSettings(raw: unknown): GodotSettings {
  const source = typeof raw === 'object' && raw !== null ? raw as Record<string, unknown> : {}
  const booleanOr = (key: 'injectLegacyMode' | 'promptGuidance' | 'zhPrompt'): boolean => {
    const value = source[key]
    return typeof value === 'boolean' ? value : SETTINGS_DEFAULTS[key]
  }
  const stringOr = (key: 'godotServerDist' | 'godotProjectPath'): string => {
    const value = source[key]
    return typeof value === 'string' ? value : SETTINGS_DEFAULTS[key]
  }
  return {
    injectLegacyMode: booleanOr('injectLegacyMode'),
    promptGuidance: booleanOr('promptGuidance'),
    zhPrompt: booleanOr('zhPrompt'),
    godotServerDist: stringOr('godotServerDist'),
    godotProjectPath: stringOr('godotProjectPath'),
  }
}

/** 从工具 schema 目录按服务器前缀收集当前全部 Godot 桥接工具名。 */
export function godotToolNames(schemas: ReadonlyArray<{ name: string }>, prefix: string): string[] {
  return schemas.filter(schema => schema.name.startsWith(prefix)).map(schema => schema.name)
}

/** 该 agent 是否应隐藏 Godot 工具(非 Godot preset 且未开原版模式)。 */
export function shouldHideAgent(options: { injectLegacyMode: boolean; presetId?: string }): boolean {
  return !options.injectLegacyMode && options.presetId !== 'godot'
}

/** 该 agent 是否需要 scoped 提示词 section(Godot preset 且非 legacy——legacy 由全局 section 覆盖)。 */
export function needsScopedPrompt(options: { injectLegacyMode: boolean; presetId?: string; promptGuidance: boolean }): boolean {
  return options.promptGuidance && !options.injectLegacyMode && options.presetId === 'godot'
}

/**
 * 工具说明解析(zhPrompt,输出不带「工具名」前缀):
 * - zh:优先「已本地化描述」(上游中文翻译表),缺失回退上游原文,最后中文占位;
 * - 非 zh:上游原文优先,再回退英文占位。
 * 说明:这是描述**翻译映射**(按名字查表),不是写死的工具清单——工具清单始终
 * 由桥接从 server 动态获取。
 */
export function resolveToolDescription(
  zh: boolean,
  localized: string | undefined,
  upstream: string | undefined,
  fallback: string,
): string {
  if (zh) return localized ?? upstream ?? '（暂无中文说明）'
  return upstream ?? fallback
}

/** 工具失败消息本地化(zhPrompt):中文包一层前缀并保留上游错误码/文本;非 zh 维持原样。 */
export function toolFailureMessage(zh: boolean, name: string, text: string): string {
  if (zh) {
    return text === '' ? `「${name}」执行失败` : `「${name}」执行失败：${text}`
  }
  return text === '' ? `${name} failed` : text
}
