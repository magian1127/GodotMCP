/**
 * 设置与挂载决策纯函数(node --test 可直接验证;host apply 与插件页共用语义)。
 *
 * 接入模型(v0.5,官方 MCP):
 * - 工具不再由本插件注册——`@deepseek-ai/dsh-mcp-client` 按官方命名
 *   `mcp__<serverName>__<rawName>` 提供。因此"某个会话是否看得到 Godot 工具"
 *   等于"该会话作用域里有没有这个 MCP 客户端",不需要 deny/restrict 补丁。
 * - 默认只有加入 Godot preset 的会话在 `agent/created` 时挂载;
 *   `injectLegacyMode`(原版模式)改为在 host 作用域挂一个**共享**连接,
 *   所有会话可见(项目路径取全局设置,不再按会话 cwd 切换)。
 */

import { GODOT_PRESET_ID } from './constants.js'

/**
 * MCP 传输模式。
 *
 * - `auto`(默认):daemon 的 2026-07-28 面可用就**直连 HTTP**(每会话零子进程),
 *   否则回退 stdio(shim)。回退只发生在"旧版 daemon / daemon 起不来"时。
 * - `http`:强制直连;端点不可用时仍回退 stdio 并记一条 warn(宁可多一个进程,
 *   也不让会话完全没有工具)。
 * - `stdio`:永远走 shim(保留给旧版本或需要 shim 协议适配的场景)。
 */
export type TransportMode = 'auto' | 'http' | 'stdio'

/**
 * daemon 会话键的作用域(决定"工具面是否跨会话共享")。
 *
 * - `session`(默认):由 DSH 会话 id 派生。每个会话从默认的 1 个工具
 *   (`godot_connect`)起步,互不影响;同一会话断线重连保留已激活的工具面。
 * - `project`:由会话 cwd 派生。同一项目的所有会话共享默认目标与激活组 ——
 *   新会话会**继承**别的会话展开后的工具面(这是共享的代价)。
 */
export type SessionKeyScope = 'session' | 'project'

/** 设置(插件行 config;host 与 client 共用默认值语义)。 */
export interface GodotSettings {
  /** 原版模式:host 作用域挂一个共享连接,所有会话可见。 */
  injectLegacyMode: boolean
  /** 是否注入工作流提示词 section(仅对挂了 Godot MCP 的会话生效)。 */
  promptGuidance: boolean
  /** 提示词中文化(section 文本;工具描述/错误消息来自上游 server,保持原文)。 */
  zhPrompt: boolean
  /** GodotMCP server 入口完整路径(空=未配置,Godot 会话不会挂载 MCP)。 */
  godotServerDist: string
  /** Godot 项目路径。**已废弃**：目标改由 daemon 侧解析，插件不再注入 `GODOT_MCP_PROJECT_PATH`；保留字段只为不让旧配置报错。 */
  godotProjectPath: string
  /** 传输模式:直连 daemon / shim / 自动。 */
  transportMode: TransportMode
  /** daemon 会话键作用域:每会话隔离 或 按项目共享。 */
  sessionKeyScope: SessionKeyScope
  /**
   * 单次工具调用超时（毫秒）。
   *
   * 这是三层预算阶梯里**宿主那一层**：daemon 长工具 180s < shim 210s < 宿主。
   * 必须大于 shim 的预算，否则慢调用会先被宿主掐断、拿不到 daemon 给出的清晰超时说明。
   * 改它会触发重挂（已纳入 mountConfigKey）。
   */
  toolCallTimeoutMs: number
}

export const SETTINGS_DEFAULTS: GodotSettings = {
  injectLegacyMode: false,
  promptGuidance: true,
  zhPrompt: false,
  godotServerDist: '',
  godotProjectPath: '',
  transportMode: 'auto',
  sessionKeyScope: 'session',
  toolCallTimeoutMs: 240_000,
}

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
  const transportOr = (): TransportMode => {
    const value = source.transportMode
    return value === 'http' || value === 'stdio' || value === 'auto' ? value : SETTINGS_DEFAULTS.transportMode
  }
  const scopeOr = (): SessionKeyScope => {
    const value = source.sessionKeyScope
    return value === 'project' || value === 'session' ? value : SETTINGS_DEFAULTS.sessionKeyScope
  }
  // 超时接受 number 或数字字符串(配置表单的输入框给出的是字符串)。
  // 非法/非正数回退默认;上限 900s —— 再长就失去了"及时失败"的意义。
  const timeoutOrDefault = (): number => {
    const raw = source.toolCallTimeoutMs
    const parsed = typeof raw === 'number' ? raw : typeof raw === 'string' ? Number(raw) : Number.NaN
    return Number.isFinite(parsed) && parsed > 0
      ? Math.min(Math.round(parsed), 900_000)
      : SETTINGS_DEFAULTS.toolCallTimeoutMs
  }
  return {
    injectLegacyMode: booleanOr('injectLegacyMode'),
    promptGuidance: booleanOr('promptGuidance'),
    zhPrompt: booleanOr('zhPrompt'),
    godotServerDist: stringOr('godotServerDist'),
    godotProjectPath: stringOr('godotProjectPath'),
    transportMode: transportOr(),
    sessionKeyScope: scopeOr(),
    toolCallTimeoutMs: timeoutOrDefault(),
  }
}

/**
 * 官方 mcp-client 的工具名前缀:`mcp__<serverName>__`。
 *
 * 命名是官方固定约定(不是本插件拼出来的门面),这里只用于提示词里描述工具名。
 */
export function toolNamePrefix(serverName: string): string {
  return `mcp__${serverName}__`
}

/** 该 preset 是否是 Godot preset(会话维度挂载的唯一判据)。 */
export function isGodotPreset(presetId: string | undefined): boolean {
  return presetId === GODOT_PRESET_ID
}

/**
 * 该作用域是否需要工作流提示词 section。
 * - Godot preset 会话:需要(scoped section)。
 * - 原版模式:需要(host 全局 section),与 preset 无关。
 */
export function shouldInjectPrompt(options: {
  injectLegacyMode: boolean
  presetId?: string
  promptGuidance: boolean
}): boolean {
  if (!options.promptGuidance) return false
  return options.injectLegacyMode || isGodotPreset(options.presetId)
}
