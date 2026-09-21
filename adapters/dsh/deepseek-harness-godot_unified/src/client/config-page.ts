// 插件页配置表单(侧栏插件页 → 本组合包页面,plugins.bundle.config 槽位)。
// DSH 0.1.6 起插件配置从设置页「插件设置」区(settings.plugin.item,已退役)
// 迁到插件页:以 npm 包名为 key 注册本表单,页面自画标题/图标/面包屑。
// 对齐官方 PluginConfigForm 约定:summary 视图渲染一句话简介,page 视图渲染
// 表单;只有保存才写入,离开页面即丢弃草稿(无放弃/恢复默认值控件)。
// 字段为 Godot 注入策略三开关 + 「Godot 路径」区。
// 「Godot 路径」区(server dist 全局单值 + 项目路径按工作目录批量条目)经同源
// /godot-workbench/api/path-settings 读写 host 持久(PathStore/paths.json),
// 不走 localStorage——桥接 server 在 host 启动、需 host 读到路径。
import React from 'react'
import { api, type ProjectEntry } from './api.js'

const h = React.createElement

export const SETTINGS_NAMESPACE = 'godot'
const LOCALE_NAMESPACE = 'settings.godot'

/** plugins.bundle.config 槽位的键:profile 中本包的 npm 包名(非 settings 命名空间)。 */
export const BUNDLE_PACKAGE_NAME = 'deepseek-harness-godot_unified'

const FIELDS = ['injectLegacyMode', 'promptGuidance', 'zhPrompt']

const DEFAULTS = {
  injectLegacyMode: false,
  promptGuidance: true,
  zhPrompt: false,
  godotServerDist: '',
  godotProjectPath: '',
}

const zh = {
  summary: 'GodotMCP 桥接的工具注入策略、提示词与相关路径。',
  loading: '正在读取设置…',
  unavailable: '当前部署未提供 Godot 设置。',
  readOnly: '当前设置文档为只读，无法保存更改。',
  injectLegacyMode: '注入原版模式',
  injectLegacyModeDesc: '开启后所有会话注入 godot_* 工具与工作流提示词（原版全局行为）。默认关闭：仅 Godot 预设的会话注入，其余会话隐藏这些工具。',
  promptGuidance: '注入工作流提示词',
  promptGuidanceDesc: '在系统提示中说明 Godot 桥接的按需扩面、FIFO 串行与错误恢复。仅对会注入工具的会话生效（Godot 预设或原版模式）。',
  zhPrompt: '提示词中文化',
  zhPromptDesc: '开启后注入的系统提示词、工具说明及其错误消息使用中文（默认英文，与内置工具一致）。',
  godotServerDist: 'Server dist 路径',
  godotServerDistDesc: 'GodotMCP server 的 dist/index.js 完整路径。为空时工具调用提示未配置；填写后首次调用自动启动桥接（空闲 10 分钟自动关闭）。',
  godotProjectPath: 'Godot 项目路径',
  godotProjectPathDesc: '目标 Godot 项目（含 project.godot）的完整路径，作为桥接的项目上下文。',
  inherited: '继承默认值',
  overridden: '用户覆盖',
  save: '保存',
  saving: '保存中…',
  saveFailed: '保存失败：',
  saveTimeout: '保存超时：写入可能已生效，刷新页面后请核对开关状态；若反复出现请反馈。',
  saveNotApplied: '主程序未接受全部设置，已保留草稿。',
  pathTitle: '相关路径',
  pathDesc: 'server dist 为全局单值；项目路径按工作目录（cwd）批量条目，切换会话时按其工作目录匹配。未设置时回退 CLI 解析链。',
  pathServerDist: 'Server dist（全局）',
  pathServerDistPlaceholder: '.../dist/index.js',
  pathProjects: '项目路径（按工作目录）',
  pathProjectsDesc: '每条 { cwd, projectPath }，可多条并存、按 cwd 唯一；重复/空 cwd 标红不保存。',
  pathAddProject: '添加',
  pathRemove: '删除',
  pathCwdPlaceholder: '工作目录 cwd',
  pathProjectPlaceholder: '.../project.godot',
  pathInvalid: 'cwd 与项目路径都必填且不能重复，非法条目不保存',
  pathEmpty: '尚未添加按工作目录的项目条目',
}

const en = {
  summary: 'Tool-injection policy, prompt, and related paths for the GodotMCP bridge.',
  loading: 'Reading settings…',
  unavailable: 'This deployment does not provide Godot settings.',
  readOnly: 'The settings document is read-only; changes cannot be saved.',
  injectLegacyMode: 'Legacy injection mode',
  injectLegacyModeDesc: 'When on, every session sees the godot_* tools and the workflow prompt (original global behavior). Default off: only sessions on the Godot preset inject; all other sessions hide these tools.',
  promptGuidance: 'Workflow prompt',
  promptGuidanceDesc: 'Explain on-demand group activation, editor FIFO ordering, and error recovery in the system prompt. Applies only to sessions that inject tools (Godot preset or legacy mode).',
  zhPrompt: 'Localize prompt',
  zhPromptDesc: 'When on, the injected system prompt, tool descriptions, and error messages use Chinese (English by default, matching built-in tools).',
  godotServerDist: 'Server dist path',
  godotServerDistDesc: 'Full path to the GodotMCP server dist/index.js. Empty = tool calls report "not configured"; once set, the bridge starts on the first call (auto-closes after 10 idle minutes).',
  godotProjectPath: 'Godot project path',
  godotProjectPathDesc: 'Full path of the target Godot project (containing project.godot), used as the bridge project context.',
  inherited: 'Inherited',
  overridden: 'Overridden',
  save: 'Save',
  saving: 'Saving...',
  saveFailed: 'Save failed:',
  saveTimeout: 'Save timed out: the write may have landed; refresh and re-check the toggles. If it repeats, please report.',
  saveNotApplied: 'The host did not accept all settings; the draft was kept.',
  pathTitle: 'Related paths',
  pathDesc: 'Server dist is a single global value; project paths are batch entries keyed by workspace (cwd), matched when you switch sessions. Falls back to the CLI resolve chain when unset.',
  pathServerDist: 'Server dist (global)',
  pathServerDistPlaceholder: '.../dist/index.js',
  pathProjects: 'Project paths (by workspace)',
  pathProjectsDesc: 'Each entry is { cwd, projectPath }; multiple allowed, unique by cwd; empty/duplicate cwd is marked red and not saved.',
  pathAddProject: 'Add',
  pathRemove: 'Remove',
  pathCwdPlaceholder: 'workspace cwd',
  pathProjectPlaceholder: '.../project.godot',
  pathInvalid: 'cwd and project path are both required and must not repeat; invalid rows are not saved',
  pathEmpty: 'No workspace-keyed project entries yet',
}

const DICTS = { zh, en }

/** 项目条目行无效判定:空 cwd/projectPath 或 cwd 重复(非空)即无效。 */
function projectInvalidAt(list: ProjectEntry[], index: number): boolean {
  const cur = list[index]
  if (cur === undefined) return true
  if (cur.cwd.trim() === '' || cur.projectPath.trim() === '') return true
  return list.some((q, j) => j !== index && q.cwd.trim() !== '' && q.cwd.trim() === cur.cwd.trim())
}

/** 整份项目条目是否存在无效行(用于保存前的整体校验)。 */
function hasInvalidProjects(list: ProjectEntry[]): boolean {
  return list.some((_, i) => projectInvalidAt(list, i))
}

/** 路径区 dirty 判定:当前草稿与「已加载/已保存」基线做规范化比较(去首尾空白)。 */
function samePaths(
  serverDist: string, projects: ProjectEntry[],
  savedServerDist: string, savedProjects: ProjectEntry[],
): boolean {
  if (serverDist.trim() !== savedServerDist.trim()) return false
  if (projects.length !== savedProjects.length) return false
  return projects.every((p, i) => p.cwd.trim() === savedProjects[i].cwd.trim() && p.projectPath.trim() === savedProjects[i].projectPath.trim())
}

interface PathSectionProps {
  t: (key: string) => string
  serverDist: string
  projects: ProjectEntry[]
  loaded: boolean
  loadError: string | null
  onServerDistChange: (value: string) => void
  onUpdateProject: (index: number, field: 'cwd' | 'projectPath', value: string) => void
  onRemoveProject: (index: number) => void
  onAddProject: (cwd: string, projectPath: string) => void
}

/** 「Godot 路径」区:server dist 全局单值 + 项目路径按工作目录(cwd)批量条目。
 * 受控、纯渲染组件:自身不持有 serverDist/projects 持久状态、不自动保存、
 * 也没有独立「保存路径」按钮——数据由配置页(createForm)统一管理,保存统一由
 * 页面「保存」按钮触发,经 /godot-workbench/api/path-settings 写 host。
 * 只保留添加行局部输入态与校验提示;每条目编辑/删除经回调上抛。 */
function PathSettingsSection(props: PathSectionProps): React.ReactElement {
  const t = props.t
  const [addCwd, setAddCwd] = React.useState('')
  const [addProjectPath, setAddProjectPath] = React.useState('')
  const [addInvalid, setAddInvalid] = React.useState(false)

  const invalidIndexes = props.projects.map((_, i) => i).filter(i => projectInvalidAt(props.projects, i))

  const addProject = (): void => {
    const cwdTrim = addCwd.trim()
    const projectPathTrim = addProjectPath.trim()
    if (cwdTrim === '' || projectPathTrim === '' || props.projects.some(p => p.cwd.trim() === cwdTrim)) {
      setAddInvalid(true)
      return
    }
    setAddInvalid(false)
    props.onAddProject(cwdTrim, projectPathTrim)
    setAddCwd('')
    setAddProjectPath('')
  }

  const sectionStyle = { marginTop: 10, borderTop: '1px solid var(--dsw-alias-border-l2)', paddingTop: 10 }
  const subTitleStyle = { fontSize: 13, fontWeight: 600, lineHeight: 1.5, marginBottom: 2 }
  const descStyle = { fontSize: 12, lineHeight: 1.5, color: 'var(--dsw-alias-label-tertiary)', marginBottom: 8 }
  const addRowStyle = { display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', marginBottom: 6 }
  const itemRowStyle = { display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', marginBottom: 4 }
  const invalidHintStyle = { fontSize: 12, lineHeight: 1.5, color: 'var(--dsw-alias-state-error-primary, #d93026)', margin: '0 0 6px' }

  const inputFor = (value: string, placeholder: string, ariaLabel: string, invalid: boolean, onChange: (v: string) => void): React.ReactElement =>
    h('input', {
      type: 'text',
      value,
      placeholder,
      'aria-label': ariaLabel,
      spellCheck: false,
      style: Object.assign({}, uiStyles.input, {
        marginTop: 0, flex: '1 1 140px', minWidth: 0,
        borderColor: invalid ? 'var(--dsw-alias-state-error-primary, #d93026)' : undefined,
      }),
      onChange: function (event: { target: { value: string } }) { onChange(event.target.value) },
    })
  const addButton = (label: string): React.ReactElement =>
    h('button', {
      type: 'button',
      style: Object.assign({}, uiStyles.primaryButton, { flex: 'none' }),
      onClick: addProject,
    }, label)
  const removeButton = (label: string, index: number): React.ReactElement =>
    h('button', {
      type: 'button',
      'aria-label': label,
      style: Object.assign({}, uiStyles.secondaryButton, { flex: 'none', padding: '3px 10px', fontSize: 12 }),
      onClick: function () { props.onRemoveProject(index) },
    }, label)

  return h('div', { style: sectionStyle, 'data-godot-paths': '' },
    h('div', { style: subTitleStyle }, t('pathTitle')),
    h('div', { style: descStyle }, t('pathDesc')),
    // server dist 全局单值。
    h('div', { style: { marginBottom: 10 } },
      h('label', { style: { display: 'block', fontSize: 12, lineHeight: 1.5, marginBottom: 2 } }, t('pathServerDist')),
      inputFor(props.serverDist, t('pathServerDistPlaceholder'), t('pathServerDist'), false, props.onServerDistChange),
    ),
    // 项目路径(按工作目录)批量条目。
    h('div', { style: subTitleStyle }, t('pathProjects')),
    h('div', { style: descStyle }, t('pathProjectsDesc')),
    h('div', { style: addRowStyle },
      inputFor(addCwd, t('pathCwdPlaceholder'), t('pathCwdPlaceholder'), addInvalid, function (v) { setAddCwd(v); setAddInvalid(false) }),
      inputFor(addProjectPath, t('pathProjectPlaceholder'), t('pathProjectPlaceholder'), addInvalid, function (v) { setAddProjectPath(v); setAddInvalid(false) }),
      addButton(t('pathAddProject')),
    ),
    addInvalid && h('div', { style: invalidHintStyle }, t('pathInvalid')),
    invalidIndexes.length > 0 && h('div', { style: invalidHintStyle }, t('pathInvalid')),
    props.projects.length === 0
      ? h('div', { style: descStyle }, t('pathEmpty'))
      : props.projects.map((item, index) => h('div', { style: itemRowStyle, key: `${item.cwd}-${index}` },
          inputFor(item.cwd, t('pathCwdPlaceholder'), t('pathCwdPlaceholder'), invalidIndexes.includes(index), function (v) { props.onUpdateProject(index, 'cwd', v) }),
          inputFor(item.projectPath, t('pathProjectPlaceholder'), t('pathProjectPlaceholder'), invalidIndexes.includes(index), function (v) { props.onUpdateProject(index, 'projectPath', v) }),
          removeButton(t('pathRemove'), index),
        )),
    h('div', { style: { display: 'flex', alignItems: 'center', gap: 8, marginTop: 8 } },
      (props.loadError !== null || !props.loaded) && h('span', { style: descStyle },
        !props.loaded ? t('loading') : `${t('saveFailed')}${props.loadError ?? ''}`)),
  )
}

// 页面表单样式:对齐官方 PluginConfigForm(容器 flex column,行/输入沿用
// 原 --dsw-alias-* token,明暗主题自适配)。
const uiStyles = {
  status: { margin: '12px 0', fontSize: 13, lineHeight: 1.5, color: 'var(--dsw-alias-label-tertiary)' },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: 14,
    minHeight: 58,
    padding: '10px 0',
    borderBottom: '1px solid var(--dsw-alias-border-l2)',
  },
  rowText: { flex: 1, minWidth: 0 },
  labelLine: { display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 7 },
  label: { fontSize: 13, fontWeight: 600, lineHeight: 1.5 },
  override: { fontSize: 11, lineHeight: 1.4, color: 'var(--dsw-alias-label-tertiary)' },
  hint: { marginTop: 2, fontSize: 12, lineHeight: 1.5, color: 'var(--dsw-alias-label-tertiary)' },
  checkbox: { flex: 'none', width: 18, height: 18, accentColor: 'var(--dsw-alias-brand-primary)' },
  input: {
    width: '100%',
    height: 32,
    boxSizing: 'border-box',
    marginTop: 6,
    border: '1px solid var(--dsw-alias-border-l2)',
    borderRadius: 7,
    padding: '4px 9px',
    background: 'var(--dsw-alias-bg-layer-3)',
    color: 'var(--dsw-alias-label-primary)',
    font: 'inherit',
    fontSize: 12,
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    flexWrap: 'wrap',
    gap: 8,
    paddingTop: 16,
  },
  error: { flex: '1 1 220px', margin: 0, fontSize: 12, lineHeight: 1.5, color: 'var(--dsw-alias-label-error)' },
  secondaryButton: {
    appearance: 'none',
    border: '1px solid var(--dsw-alias-border-l2)',
    borderRadius: 8,
    padding: '5px 12px',
    background: 'transparent',
    color: 'var(--dsw-alias-label-secondary)',
    font: 'inherit',
    fontSize: 13,
    lineHeight: 1.5,
    cursor: 'pointer',
  },
  primaryButton: {
    appearance: 'none',
    border: '1px solid transparent',
    borderRadius: 8,
    padding: '5px 14px',
    // 反色按钮:主文字色做底、层底色做字,任何主题下对比正确(官方 .save 同款)。
    background: 'var(--dsw-alias-label-primary)',
    color: 'var(--dsw-alias-bg-layer-3)',
    font: 'inherit',
    fontSize: 13,
    lineHeight: 1.5,
    cursor: 'pointer',
  },
}

function controlDisabledStyle(disabled: boolean): { opacity: number; cursor: string } | null {
  return disabled ? { opacity: 0.45, cursor: 'default' } : null
}

interface SettingsValue {
  injectLegacyMode: boolean
  promptGuidance: boolean
  zhPrompt: boolean
}

function normalized(value: unknown): SettingsValue {
  const source = typeof value === 'object' && value !== null ? value as Record<string, unknown> : {}
  const booleanOr = (key: keyof SettingsValue): boolean =>
    typeof source[key] === 'boolean' ? source[key] as boolean : DEFAULTS[key]
  return {
    injectLegacyMode: booleanOr('injectLegacyMode'),
    promptGuidance: booleanOr('promptGuidance'),
    zhPrompt: booleanOr('zhPrompt'),
  }
}

function sameSettings(left: SettingsValue, right: SettingsValue): boolean {
  return FIELDS.every(field => Object.is(left[field as keyof SettingsValue], right[field as keyof SettingsValue]))
}

function hasOwn(value: unknown, key: string): boolean {
  return typeof value === 'object' && value !== null && Object.prototype.hasOwnProperty.call(value, key)
}

function createForm(scope: any, t: (key: string) => string): (props: { view?: string }) => any {
  function FieldIdentity(props: { overridden: boolean }) {
    return h('span', { style: uiStyles.override, 'data-godot-override': '' }, props.overridden ? t('overridden') : t('inherited'))
  }

  function ToggleRow(props: { field: string; value: boolean; disabled: boolean; overridden: boolean; onChange: (next: boolean) => void }) {
    return h('label', { style: uiStyles.row, 'data-godot-row': '' },
      h('span', { style: uiStyles.rowText },
        h('span', { style: uiStyles.labelLine, 'data-godot-label-line': '' },
          h('span', { style: uiStyles.label }, t(props.field)),
          h(FieldIdentity, { overridden: props.overridden })),
        h('span', { style: uiStyles.hint, 'data-godot-hint': '' }, t(props.field + 'Desc'))),
      h('input', {
        type: 'checkbox',
        role: 'switch',
        checked: props.value,
        disabled: props.disabled,
        'aria-label': t(props.field),
        style: Object.assign({}, uiStyles.checkbox, controlDisabledStyle(props.disabled)),
        onChange: function (event: { target: { checked: boolean } }) { props.onChange(event.target.checked) },
      }))
  }

  return function GodotConfigPage(props: { view?: string }) {
    // summary 视图:插件页标题下的一句话简介;组合包页面目前只用 page 视图,
    // summary 分支作为契约兜底(契约声明两种视图都会传)。
    if (props?.view === 'summary') return h('span', { 'data-godot-summary': '' }, t('summary'))
    const snapshot = React.useSyncExternalStore(
      function (listener: () => void) { return scope.subscribe(listener) },
      function () { return scope.getSnapshot() },
    )
    const value = normalized(snapshot.value)
    const [draft, setDraft] = React.useState(function () { return value })
    const [saving, setSaving] = React.useState(false)
    const [error, setError] = React.useState('')
    // 「Godot 路径」区草稿(受表单统一管理):serverDist 全局单值 + 项目路径批量条目。
    const [serverDist, setServerDist] = React.useState('')
    const [projects, setProjects] = React.useState<ProjectEntry[]>([])
    const [pathsLoaded, setPathsLoaded] = React.useState(false)
    const [pathsLoadError, setPathsLoadError] = React.useState<string | null>(null)
    // 路径「已保存」基线:初始为挂载时 pathSettings() 读到的值,保存成功后更新。
    const [savedServerDist, setSavedServerDist] = React.useState('')
    const [savedProjects, setSavedProjects] = React.useState<ProjectEntry[]>([])
    const pathsDirty = !samePaths(serverDist, projects, savedServerDist, savedProjects)
    const dirty = !sameSettings(draft, value) || pathsDirty
    const editable = snapshot.status === 'ready' && snapshot.writable && !saving

    React.useEffect(function () {
      if (!dirty && !saving && snapshot.status === 'ready') setDraft(normalized(snapshot.value))
    }, [snapshot.revision, snapshot.status, dirty, saving])

    // 挂载时读取整份路径配置(不带 cwd,返回全局 serverDist + 整份 projects)。
    React.useEffect(function () {
      let alive = true
      void api.pathSettings()
        .then(p => {
          if (!alive) return
          const sDist = p.serverDist ?? ''
          const projs = (p.projects ?? []).map(pj => ({ cwd: pj.cwd, projectPath: pj.projectPath }))
          setServerDist(sDist)
          setProjects(projs)
          setSavedServerDist(sDist)
          setSavedProjects(projs)
          setPathsLoaded(true)
          setPathsLoadError(null)
        })
        .catch(error => {
          if (!alive) return
          setPathsLoaded(true)
          setPathsLoadError(error instanceof Error ? error.message : String(error))
        })
      return function () { alive = false }
    }, [])

    function change(field: string, nextValue: any) {
      setError('')
      setDraft(function (previous: SettingsValue) {
        return Object.assign({}, previous, { [field]: nextValue })
      })
    }

    // 路径区改动回调:仅更新草稿并清错误,不落盘(保存由「保存」按钮触发)。
    function changeServerDist(nextValue: string) {
      setError('')
      setServerDist(nextValue)
    }
    function updateProject(index: number, field: 'cwd' | 'projectPath', value: string) {
      setError('')
      setProjects(prev => prev.map((p, i) => (i === index ? { ...p, [field]: value } : p)))
    }
    function removeProject(index: number) {
      setError('')
      setProjects(prev => prev.filter((_, i) => i !== index))
    }
    function addProject(cwd: string, projectPath: string) {
      setError('')
      setProjects(prev => [...prev, { cwd, projectPath }])
    }

    // 保存:先写三个开关(watch 覆盖),随后把「相关路径」整份 POST 到 pathStore。
    // 路径保存失败(含非法行)在表单 error 区可见;开关已先落盘则保留。
    async function save() {
      if (!editable || !dirty) return
      setSaving(true)
      setError('')
      try {
        for (const field of FIELDS) {
          if (!Object.is(draft[field as keyof SettingsValue], value[field as keyof SettingsValue])) {
            // 写入有界等待:remote 应答异常丢失时不能把表单永久钉在"保存中"
            // (值通常已落盘,超时后刷新页面即可看到)。
            await Promise.race([
              scope.set(field, draft[field as keyof SettingsValue]),
              new Promise<'timeout'>((resolveTimeout) => { setTimeout(() => resolveTimeout('timeout'), 10_000) }),
            ]).then(settled => {
              if (settled === 'timeout') throw new Error('timeout')
            })
          }
        }
        const accepted = normalized(scope.getSnapshot().value)
        if (!sameSettings(accepted, draft)) throw new Error(t('saveNotApplied'))
        // 相关路径:整份覆盖。非法条目(cwd/projectPath 为空或 cwd 重复)不提交。
        if (hasInvalidProjects(projects)) throw new Error(t('pathInvalid'))
        const sDist = serverDist.trim()
        const projs = projects.map(p => ({ cwd: p.cwd.trim(), projectPath: p.projectPath.trim() }))
        await api.savePathSettings(sDist, projs)
        setSavedServerDist(sDist)
        setSavedProjects(projs)
      } catch (saveError) {
        setError(saveError instanceof Error ? saveError.message : String(saveError))
      } finally {
        setSaving(false)
      }
    }

    // 离开页面即卸载,草稿随组件状态丢弃(官方约定:只有保存才写入)。
    return h('div', { style: { display: 'flex', flexDirection: 'column' }, 'data-godot-config': '' },
      snapshot.status === 'loading'
        ? h('p', { style: uiStyles.status }, t('loading'))
        : snapshot.status !== 'ready'
          ? h('p', { style: uiStyles.status }, t('unavailable'))
          : h(React.Fragment, null,
              snapshot.writable ? null : h('p', { style: uiStyles.status }, t('readOnly')),
              h(ToggleRow, {
                field: 'injectLegacyMode', value: draft.injectLegacyMode, disabled: !editable,
                overridden: hasOwn(snapshot.user, 'injectLegacyMode'),
                onChange: function (next: boolean) { change('injectLegacyMode', next) },
              }),
              h(ToggleRow, {
                field: 'promptGuidance', value: draft.promptGuidance, disabled: !editable,
                overridden: hasOwn(snapshot.user, 'promptGuidance'),
                onChange: function (next: boolean) { change('promptGuidance', next) },
              }),
              h(ToggleRow, {
                field: 'zhPrompt', value: draft.zhPrompt, disabled: !editable,
                overridden: hasOwn(snapshot.user, 'zhPrompt'),
                onChange: function (next: boolean) { change('zhPrompt', next) },
              }),
              h(PathSettingsSection, {
                t,
                serverDist,
                projects,
                loaded: pathsLoaded,
                loadError: pathsLoadError,
                onServerDistChange: changeServerDist,
                onUpdateProject: updateProject,
                onRemoveProject: removeProject,
                onAddProject: addProject,
              }),
              h('div', { style: uiStyles.footer, 'data-godot-footer': '' },
                h('p', { style: uiStyles.error }, error === '' ? '' : t('saveFailed') + error),
                h('button', {
                  type: 'button',
                  disabled: !editable || !dirty,
                  style: Object.assign({}, uiStyles.primaryButton, controlDisabledStyle(!editable || !dirty)),
                  onClick: function () { void save() },
                }, saving ? t('saving') : t('save')))))
  }
}

/** 注册插件页配置表单(侧栏插件页 → 本组合包页面)。 */
export function registerConfigPage(ctx: Record<string, any>): void {
  const scope = ctx.settingsScope.bind({ namespace: SETTINGS_NAMESPACE })
  const t = ctx.locale.bind(LOCALE_NAMESPACE)
  const ConfigPage = createForm(scope, t)
  ctx.effect(function () {
    return ctx.locale.register(LOCALE_NAMESPACE, DICTS)
  }, 'godot workbench: settings dictionaries')
  ctx.slots.inject('plugins.bundle.config', function () {
    return ctx.slots.register({
      name: 'plugins.bundle.config',
      key: BUNDLE_PACKAGE_NAME,
      locale: LOCALE_NAMESPACE,
    }, ConfigPage)
  })
}
