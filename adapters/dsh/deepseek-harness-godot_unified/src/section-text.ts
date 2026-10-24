/**
 * Godot 工作流提示词 section(面向模型;双语,由设置 zhPrompt 切换)。
 *
 * 内容蒸馏自 GodotMCP 的 godot-control / godot-playtest 技能:
 * 按需扩面、批量编辑、FIFO 串行语义、错误恢复与截图验证。
 *
 * v0.5 起工具由宿主官方 MCP 客户端提供,名字是 `mcp__<serverName>__<rawName>`
 * (默认 `mcp__godot__*`)——本文件不列工具清单,只描述工作流与工具名形状。
 */
import { toolNamePrefix } from './injection.js'

export function sectionText(serverName: string = 'godot', zh: boolean = false): string {
  const prefix = toolNamePrefix(serverName)
  const discover = `${prefix}discover_tools`
  if (zh) {
    return [
      `Godot 工作流（${prefix}* 工具，当存在时）：`,
      `- 这些工具由 DSH 的 MCP 客户端提供，连接、重连与工具清单更新都由宿主管理；工具名为 ${prefix}<原生工具名>（例如 ${discover}）。`,
      `- 常驻工具列表刻意保持精简。需要更多能力时，调用 ${discover} 搜索并激活工具组（31 组：3d、animation、audio、debugger、input_map、lsp 分析/导航、particles、scene advanced、tilemap、theme 等）。组名或关键词通过 request 参数传入（字符串或数组）；activate 是布尔开关（默认 true 自动激活，false 仅浏览）——不要把组名放进 activate；释放用 reset。激活的组会把工具加入本会话的工具集——只激活当前任务需要的组。`,
      `- 每个工具的必填参数以其参数 schema 为准：不确定参数名、类型或必填项时，先调用 ${discover}（include_schemas=true 或查看工具描述）确认，不要凭记忆猜参数（例如缺 mode 等必填枚举会直接报 Invalid option）。`,
      `- 编辑器内的变更操作由编辑器内部 FIFO 队列串行执行。调用返回“已接受并排队（accepted and queued）”不是错误；等待后重新读取状态即可，不要重复提交同一变更。`,
      `- 先用只读工具（项目信息、场景查询），再批量执行相关写入。视觉改动后用截图工具验证——截图会直接返回给你（无头编辑器返回 HEADLESS_UNSUPPORTED）。`,
      `- 错误携带 UPPER_SNAKE 错误码与可行动信息。AUTH_FAILED 或连接类失败通常意味着目标项目的 Godot 编辑器没有运行，或项目已移动：告知用户，并建议打开它（无头也可：godot --headless --editor --path <项目>）。MCP 客户端会在每次连接时重新读取会话令牌，重启后的编辑器会在下次调用时自愈。`,
      `- 不要固定编辑器/运行时端口，除非用户在两端都显式固定了。`,
      `- 若变更类工具缺失，说明 GODOT_MCP_READ_ONLY=1 生效（设计如此）；请尊重该限制，不要尝试绕过。`,
    ].join('\n')
  }
  return [
    `Godot workflow (${prefix}* tools, when present):`,
    `- DSH's MCP client provides these tools; connection, reconnects, and tool-list updates are owned by the host. Tool names are ${prefix}<native tool name> (for example ${discover}).`,
    `- The resident tool list is intentionally small. When you need more capability, call ${discover} to search and activate a tool group (31 groups: 3d, animation, audio, debugger, input_map, lsp analysis/navigation, particles, scene advanced, tilemap, theme, ...). Pass the group names or keywords via the request argument (string or array); activate is a boolean switch (default true = auto-activate, false = browse only) — never pass group names into activate; release groups with reset. Activated groups add tools to your toolset for this session — activate only what the task needs.`,
    `- Each tool's required arguments are defined by its parameter schema: when unsure about parameter names, types, or required fields, confirm via ${discover} (include_schemas=true or the tool description) rather than guessing — missing required enums like mode fail with Invalid option.`,
    `- Editor mutations are serialized by a FIFO queue inside the editor. A call reporting it was accepted and queued is NOT an error; wait and read back state instead of repeating the mutation.`,
    `- Prefer read tools first (project info, scene queries), then batch related writes. After visual changes verify with a screenshot tool — images return to you directly (headless editors return HEADLESS_UNSUPPORTED).`,
    `- Errors carry UPPER_SNAKE codes with actionable messages. AUTH_FAILED or connection failures usually mean the Godot editor for the target project is not running, or the project moved: tell the user, and suggest opening it (headless works: godot --headless --editor --path <project>). The MCP client re-reads the session token on every connection, so a restarted editor self-heals on the next call.`,
    `- Do not pin editor/runtime ports unless the user pinned them on both sides.`,
    `- If mutating tools are absent, GODOT_MCP_READ_ONLY=1 is in effect by design; respect it and do not attempt workarounds.`,
  ].join('\n')
}

/**
 * "Godot 会话但没有可挂载配置"的 section。
 *
 * 为什么要有它:工具缺失时模型至少要知道**为什么**以及该让用户做什么——
 * 否则模型会凭印象调用 `mcp__godot__*` 然后失败,或在用户面前假装有工具。
 * 该文本只在 Godot preset 会话(或原版模式全局)里出现,不打扰普通会话。
 */
export function notConfiguredSectionText(serverName: string = 'godot', zh: boolean = false): string {
  const prefix = toolNamePrefix(serverName)
  if (zh) {
    return [
      `Godot MCP 尚未连接（本次会话没有 ${prefix}* 工具）：`,
      `- server 入口路径为空，因此本插件没有为这个会话挂载 Godot MCP。这种情况下不要臆造 ${prefix}* 工具调用。`,
      `- 请让用户在侧栏「插件」页 → 本包页面的「Godot 路径」区填写 server 入口（GodotMCP 随包 shim 可执行文件的完整路径），并配置目标项目路径。`,
      `- 配置保存后无需重启：下一次进入会话或新建会话会重新挂载；若仍无工具，检查 server 入口文件是否存在、以及 JS 脚本入口是否可被当前 node 执行。`,
    ].join('\n')
  }
  return [
    `Godot MCP is not connected (this session has no ${prefix}* tools):`,
    `- The server entry path is empty, so this plugin did not mount Godot MCP for this session. Do not invent ${prefix}* tool calls in this state.`,
    `- Ask the user to fill in the server entry (the full path of the GodotMCP bundled shim executable) and the target project path under the plugin page → this package → "Godot paths".`,
    `- No restart is needed after saving: re-entering or creating a session mounts again. If tools are still missing, check that the server entry exists and that a JS entry can be executed by the current node.`,
  ].join('\n')
}
