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
  /** Godot 项目路径(作为 GODOT_MCP_PROJECT_PATH 传给 server;会话 cwd 无匹配时兜底)。 */
  godotProjectPath: string
}

export const SETTINGS_DEFAULTS: GodotSettings = {
  injectLegacyMode: false,
  promptGuidance: true,
  zhPrompt: false,
  godotServerDist: '',
  godotProjectPath: '',
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
  return {
    injectLegacyMode: booleanOr('injectLegacyMode'),
    promptGuidance: booleanOr('promptGuidance'),
    zhPrompt: booleanOr('zhPrompt'),
    godotServerDist: stringOr('godotServerDist'),
    godotProjectPath: stringOr('godotProjectPath'),
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
