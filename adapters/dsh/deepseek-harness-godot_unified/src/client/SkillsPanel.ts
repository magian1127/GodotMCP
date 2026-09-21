// 技能与提示词面(两个子 Tab;数据按需拉取)。
import React from 'react'
import { api, type SkillDoc } from './api.js'

const h = React.createElement

export interface SkillsPanelProps {
  lang: 'zh' | 'en'
  t: (key: string) => string
}

export function SkillsPanel(props: SkillsPanelProps): React.ReactElement {
  const [tab, setTab] = React.useState<'skills' | 'sections'>('skills')
  const [skills, setSkills] = React.useState<SkillDoc[] | null>(null)
  const [sections, setSections] = React.useState<Array<{ name: string; text: string }> | null>(null)
  const [open, setOpen] = React.useState<string | null>(null)
  const load = (): void => {
    if (tab === 'skills') void api.skills(props.lang).then(r => { setSkills(r.skills) }).catch(() => { setSkills([]) })
    else void api.promptSections().then(r => { setSections(r.sections) }).catch(() => { setSections([]) })
  }
  React.useEffect(load, [tab, props.lang])
  const list = tab === 'skills' ? skills : sections
  return h('div', { style: { display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 } },
    h('div', { className: 'gwb-tabs' },
      h('button', { className: 'gwb-tab' + (tab === 'skills' ? ' on' : ''), onClick: () => { setTab('skills') } }, props.t('skills')),
      h('button', { className: 'gwb-tab' + (tab === 'sections' ? ' on' : ''), onClick: () => { setTab('sections') } }, props.t('sections')),
      h('button', { className: 'gwb-btn', style: { marginLeft: 'auto' }, onClick: load }, props.t('promptRefresh')),
    ),
    h('div', { className: 'gwb-body' },
      list === null
        ? h('div', { className: 'gwb-empty' }, props.t('loading'))
        : tab === 'skills'
          ? (skills ?? []).length === 0
            ? h('div', { className: 'gwb-empty' }, props.t('installHint'))
            : (skills ?? []).map(skill => h('div', { key: skill.id, className: 'gwb-skill' },
                h('button', { onClick: () => { setOpen(open === skill.id ? null : skill.id) } }, open === skill.id ? '▾ ' : '▸ ', skill.title),
                open === skill.id && h('div', { className: 'gwb-skill-body' }, skill.content),
              ))
          : h('div', null, (sections ?? []).map(section => h('div', { key: section.name, className: 'gwb-skill' },
              h('button', { onClick: () => { setOpen(open === section.name ? null : section.name) } }, open === section.name ? '▾ ' : '▸ ', section.name),
              open === section.name && h('div', { className: 'gwb-skill-body' }, section.text),
            ))),
    ),
  )
}
