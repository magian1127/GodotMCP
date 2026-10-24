import { createRequire } from 'node:module'

/** 包名(与 package.json name 一致;tests 对齐断言守护)。 */
export const PKG = 'deepseek-harness-godot_unified'

/** 包版本:优先运行时读包内 package.json(发布后与真实版本恒一致);
 * 读不到(测试布局/异常环境)回退硬编码字面量,避免版本号漂移。 */
export const VERSION: string = (() => {
  try {
    const requireSelf = createRequire(import.meta.url)
    const pkg = requireSelf('../package.json') as { version?: string } | undefined
    return typeof pkg?.version === 'string' && pkg.version !== '' ? pkg.version : '0.5.0'
  } catch {
    return '0.5.0'
  }
})()

/** bundle patch 行 id(cordis.patch.yml 的 insert 条目)。 */
export const BUNDLE_ROW_ID = 'dsh-godot'

/** 提示词 section 名称(systemPrompt 命名空间内)。 */
export const SECTION_NAME = 'godot-mcp-workflow'

/**
 * section 排序权重。本段是新增段(不是对官方段的同名替换),按产品选择放在
 * 110:紧跟 persona 之后、早于具体工具说明区,作为会话初始的 Godot 能力/工作流
 * 总览。它不属于具体工具说明,因此不套用工具区(≥1000)的档位。
 * 替换型段(zh_pro/智谱的 `tool:web_search`、hashline 的 `tool:read`/`tool:edit`)
 * 必须沿用官方 TOOL_* 的 order,不能借用本值。
 */
export const SECTION_ORDER = 110

/**
 * Godot agent preset id(显示名 Godot;bundle patch 的 `preset-godot` 行声明它)。
 * 本插件据此判定"该会话是否需要 Godot MCP":命中才把官方 mcp-client 挂进
 * 该 agent 的作用域。
 */
export const GODOT_PRESET_ID = 'godot'

/**
 * 默认 MCP serverName。工具名由官方 mcp-client 的固定约定给出:
 * `mcp__<serverName>__<rawName>`(默认即 `mcp__godot__scene_get_tree` 这样的形状)。
 */
export const DEFAULT_SERVER_NAME = 'godot'
