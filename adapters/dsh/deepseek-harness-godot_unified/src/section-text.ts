/**
 * Model-facing Godot workflow text. Transport and tools belong to the host MCP client.
 */
import { toolNamePrefix } from './injection.js'

export function sectionText(serverName: string = 'godot', zh: boolean = false): string {
  const prefix = toolNamePrefix(serverName)
  const discover = `${prefix}discover_tools`
  const connect = `${prefix}godot_connect`
  if (zh) {
    return [
      `Godot 工作流：`,
      `- 首次或工具面退回入口时先调用 ${connect}。扩面用 ${discover}：组名以其静态描述与目录为准；request 是字符串或数组，activate:false 仅浏览，reset 释放组；只传 include_schemas:true 不会激活。`,
      `- 参数以宿主最新工具列表的 schema 为准。include_schemas:true 对部分常驻工具只返回名称，此时查看激活后的工具列表，不猜参数。工具列表未更新时核对宿主发现能力；ttlMs=0 不能保证自动重拉。`,
      `- 通过 scene_edit 编辑场景，code_intel 做脚本检查/LSP，editor_ops 做文件刷新，playtest 操作游戏。普通工作同时激活的组建议不超过约 5 个，阶段结束后 reset 释放不再需要的组。`,
      `- 任何 UI 的创建或修改（Control 节点、主题/样式、响应式布局、菜单/HUD/背包/对话等界面）动手前必须先阅读 godot-ui 技能。`,
      `- 目标和组属于 shim 会话；多项目交错调用显式传 instance，同一会话顺序完成目标切换和工具调用。daemon 重启后重新核对目标与组，不假定最近一帧重放恢复了全部增量状态。`,
      `- 编辑器变更经 FIFO 串行执行。已接受并排队不是错误；超时或传输中断后先查询状态，再决定是否重提，避免同一变更执行两次。视觉改动用截图验证；无头模式不证明渲染结果。`,
      `- TARGET_NOT_RUNNING/NO_INSTANCE 时打开对应 Godot 编辑器；SESSION_HEADER_MISSING 等会话头错误检查 stdio shim 配置。daemon 负责读取编辑器令牌；本插件不分发凭据。`,
      `- 工具缺失先检查是否连接、对应组是否激活、版本门控及宿主目录是否刷新。遇到明确的只读限制应尊重它；unsafe 需 daemon 显式启用且操作已获授权。`,
    ].join('\n')
  }
  return [
    `Godot workflow:`,
    `- On first use or a surface reset, call ${connect} first. Expand with ${discover}: group names follow its static description/catalog; request is a string/array, activate:false browses, reset releases groups; include_schemas:true alone activates nothing.`,
    `- Use schemas from the host's latest tool list. include_schemas:true returns names alone for some resident tools; inspect the activated list rather than guessing. Check host discovery when lists remain stale; ttlMs=0 does not guarantee refresh.`,
    `- Load scene_edit for authoring, code_intel for script checks/LSP, editor_ops for file refresh, playtest for game operations. Keep roughly five groups active at most in ordinary work and reset groups no longer needed at phase boundaries.`,
    `- Before creating or modifying any UI (Control nodes, themes/styles, responsive layout, menus/HUD/inventory/dialogs), read the godot-ui skill first.`,
    `- Targets/groups belong to the shim session. Pass instance when interleaving projects and sequence target switches/calls within one session. Recheck target/groups after daemon restart; last-frame replay cannot reconstruct every incremental change.`,
    `- Editor mutations run through FIFO. Accepted-and-queued is not an error. After timeouts/transport loss, read state before resubmitting to avoid duplicate mutations. Verify visual changes with screenshots; headless checks do not prove rendering.`,
    `- For TARGET_NOT_RUNNING/NO_INSTANCE open the target editor. For SESSION_HEADER_MISSING and related errors inspect the stdio shim configuration. The daemon reads editor tokens; this adapter does not distribute credentials.`,
    `- For missing tools check connection, active groups, engine-version gates, and host refresh. Respect explicit read-only restrictions. Unsafe requires daemon opt-in and current user authorization.`,
  ].join('\n')
}

export function notConfiguredSectionText(serverName: string = 'godot', zh: boolean = false): string {
  const prefix = toolNamePrefix(serverName)
  if (zh) {
    return [
      `Godot MCP 尚未连接（本次会话没有 ${prefix}* 工具）：`,
      `- server 入口为空，因此本插件没有挂载 Godot MCP；不要臆造工具调用。`,
      `- 请在插件页的 Godot 路径区填写随包 shim 可执行文件。项目目标在挂载后由 godot_connect(project) 选择。`,
      `- 保存后核对入口文件与会话挂载状态，必要时新建 Godot preset 会话。`,
    ].join('\n')
  }
  return [
    `Godot MCP is not connected (this session has no ${prefix}* tools):`,
    `- The server entry is empty, so this plugin did not mount Godot MCP. Do not invent tool calls.`,
    `- Fill in the bundled shim executable under Godot paths on the plugin page. Select the project afterward with godot_connect(project).`,
    `- After saving, verify the entry and mount state; create a new Godot-preset session if necessary.`,
  ].join('\n')
}
