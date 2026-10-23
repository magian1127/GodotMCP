import { createRequire } from 'node:module'

/** 包名(与 package.json name 一致;tests 对齐断言守护)。 */
export const PKG = 'deepseek-harness-godot_unified'

/** 包版本:优先运行时读包内 package.json(发布后与真实版本恒一致);
 * 读不到(测试布局/异常环境)回退硬编码字面量,避免 MCP clientInfo 漂移。 */
export const VERSION: string = (() => {
  try {
    const requireSelf = createRequire(import.meta.url)
    const pkg = requireSelf('../package.json') as { version?: string } | undefined
    return typeof pkg?.version === 'string' && pkg.version !== '' ? pkg.version : '0.4.1'
  } catch {
    return '0.4.1'
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
