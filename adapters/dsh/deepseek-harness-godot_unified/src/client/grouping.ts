// 工具树分组(纯函数;client 内使用,node 测试可跑)。
export interface ToolEntry { name: string; description: string; parameters?: unknown }
export type GroupKind = 'godot-resident' | 'godot-group' | 'builtin' | 'plugin'
export interface ToolGroupView { kind: GroupKind; id: string; title: string; tools: ToolEntry[] }
export interface ToolGrouping { groups: ToolGroupView[]; godotTotal: number }

const BUILTIN_NAMES = new Set(['read', 'write', 'edit', 'glob', 'grep', 'pwsh', 'web_search', 'web_fetch', 'ask_user_question', 'todo_write', 'job_list', 'job_output', 'job_kill', 'send_message', 'skill', 'get_goal', 'update_goal', 'create_goal'])

export function groupTools(tools: ToolEntry[], godotPrefix: string, activeGroupTools: Set<string>): ToolGrouping {
  const resident: ToolEntry[] = []
  const grouped: ToolEntry[] = []
  const builtin: ToolEntry[] = []
  const plugin: ToolEntry[] = []
  let godotTotal = 0
  for (const tool of tools) {
    if (tool.name.startsWith(godotPrefix)) {
      godotTotal += 1
      if (activeGroupTools.has(tool.name)) grouped.push(tool)
      else resident.push(tool)
    } else if (BUILTIN_NAMES.has(tool.name) || tool.name.startsWith('job_') || tool.name.startsWith('subagent')) {
      builtin.push(tool)
    } else {
      plugin.push(tool)
    }
  }
  const byName = (a: ToolEntry, b: ToolEntry): number => (a.name < b.name ? -1 : 1)
  const groups: ToolGroupView[] = []
  if (resident.length > 0) groups.push({ kind: 'godot-resident', id: 'godot', title: 'Godot', tools: resident.sort(byName) })
  if (grouped.length > 0) groups.push({ kind: 'godot-group', id: 'godot-groups', title: 'Godot(已激活组)', tools: grouped.sort(byName) })
  if (builtin.length > 0) groups.push({ kind: 'builtin', id: 'builtin', title: '内置工具', tools: builtin.sort(byName) })
  if (plugin.length > 0) groups.push({ kind: 'plugin', id: 'plugin', title: '其他插件', tools: plugin.sort(byName) })
  return { groups, godotTotal }
}
