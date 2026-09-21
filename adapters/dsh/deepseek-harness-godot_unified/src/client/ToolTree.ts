// 工具树:分组(Godot 置顶)/搜索/按需组激活入口。纯展示,数据由父组件传入。
import React from 'react'
import { groupTools, type ToolEntry } from './grouping.js'
import type { GodotGroupInfo } from './api.js'

const h = React.createElement

/** 列表行内截断:完整说明放 title(tooltip),避免长中文描述(如 input_simulate)把 DOM/布局撑爆。 */
function brief(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max)}…` : text
}

export interface ToolTreeProps {
  tools: ToolEntry[]
  godotGroups: GodotGroupInfo[]
  godotPrefix: string
  selectedName: string | null
  search: string
  t: (key: string) => string
  onSearch: (value: string) => void
  onSelect: (tool: ToolEntry) => void
  onActivateGroup: (groupName: string) => void
}

export function ToolTree(props: ToolTreeProps): React.ReactElement {
  const activeNames = new Set(props.godotGroups.filter(g => g.active).flatMap(g => g.tools.map(name => `${props.godotPrefix}${name}`)))
  const keyword = props.search.trim().toLowerCase()
  const filtered = keyword === ''
    ? props.tools
    : props.tools.filter(tool => tool.name.toLowerCase().includes(keyword) || tool.description.toLowerCase().includes(keyword))
  const { groups } = groupTools(filtered, props.godotPrefix, activeNames)
  const inactive = props.godotGroups.filter(g => !g.active)
  const item = (tool: ToolEntry): React.ReactElement =>
    h('button', {
      key: tool.name, className: props.selectedName === tool.name ? 'gwb-item on' : 'gwb-item',
      onClick: () => { props.onSelect(tool) }, title: tool.description,
    }, tool.name, h('small', null, brief(tool.description, 100)))
  return h('div', { className: 'gwb-tree' },
    h('input', { className: 'gwb-search', placeholder: props.t('search'), value: props.search, onInput: (e: React.FormEvent<HTMLInputElement>) => { props.onSearch(e.currentTarget.value) } }),
    groups.map(group => h(React.Fragment, { key: group.id },
      h('div', { className: 'gwb-group' }, `${group.title} (${group.tools.length})`),
      group.tools.map(item),
    )),
    inactive.length > 0 && h(React.Fragment, null,
      h('div', { className: 'gwb-group' }, `${props.t('inactiveGroups')} (${inactive.length})`),
      inactive.map(group => h('div', { key: group.name, style: { padding: '2px 0' } },
        h('button', { className: 'gwb-item', onClick: () => { props.onActivateGroup(group.name) }, title: props.t('activateHint') },
          `▶ ${group.name}`, h('small', null, brief(group.description || `${group.tools.length} ${props.t('groupTools')}`, 60))),
      )),
    ),
  )
}
