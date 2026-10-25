// 插件页配置表单(侧栏插件页 → 本组合包页面,plugins.bundle.config 槽位)。
// DSH 0.1.6 起插件配置从设置页「插件设置」区(settings.plugin.item,已退役)
// 迁到插件页:以 npm 包名为 key 注册本表单,页面自画标题/图标/面包屑。
// 对齐官方 PluginConfigForm 约定:summary 视图渲染一句话简介,page 视图渲染
// 表单;只有保存才写入,离开页面即丢弃草稿(无放弃/恢复默认值控件)。
// 字段为 Godot 注入策略三开关 + 「Godot 路径」区。
// 「Godot 路径」区(server dist 全局单值 + 项目路径按工作目录批量条目)经同源
// /godot-config/api/path-settings 读写 host 持久(PathStore/paths.json),
// 不走 localStorage——MCP 连接在 host 侧挂载、需 host 读到路径。
import React from 'react'
import { api, type ProjectEntry } from './api.js'

const h = React.createElement

// DSH 0.1.7 起 settings 命名空间退役,配置值挂在插件行 config 上;configForms
// 服务按入口 id(profile 行 id)取 ConfigForm——与旧 settings 命名空间 'godot'
// 不同名,迁移时必须同步改绑定 id。
export const CONFIG_ENTRY_ID = 'dsh-godot'
const LOCALE_NAMESPACE = 'settings.godot'

/** plugins.bundle.config 槽位的键:profile 中本包的 npm 包名(非 settings 命名空间)。 */
export const BUNDLE_PACKAGE_NAME = 'deepseek-harness-godot_unified'

const FIELDS = ['injectLegacyMode', 'promptGuidance', 'zhPrompt', 'toolCallTimeoutMs']

const DEFAULTS = {
  injectLegacyMode: false,
  promptGuidance: true,
  zhPrompt: false,
  /** 与 injection.ts 的 SETTINGS_DEFAULTS 同值（三层阶梯的最高层）。 */
  toolCallTimeoutMs: 240_000,
}

const zh = {
  summary: 'GodotMCP 的工具挂载策略、提示词与相关路径。',
  loading: '正在读取设置…',
  unavailable: '当前部署未提供 Godot 设置。',
  readOnly: '当前设置文档为只读，无法保存更改。',
  injectLegacyMode: '原版模式（全局挂载）',
  injectLegacyModeDesc: '开启后在 host 上挂一个共享 MCP 连接：所有会话都能看到 mcp__godot__* 工具并注入工作流提示词（项目路径取全局设置）。默认关闭：只有 Godot 预设会话挂载 MCP，其余会话不是"隐藏"这些工具，而是根本没有挂载。',
  promptGuidance: '注入工作流提示词',
  promptGuidanceDesc: '在系统提示中说明 Godot 的按需扩面、编辑器 FIFO 串行与错误恢复。仅对挂了 Godot MCP 的会话生效（Godot 预设或原版模式）。',
  toolCallTimeout: '单次工具调用超时（毫秒）',
  toolCallTimeoutDesc: '默认 240000。必须大于服务器的 210000：否则慢调用会先被宿主掐断，看不到服务器给出的清晰超时说明。改动会重新建立连接。',
  zhPrompt: '提示词中文化',
  zhPromptDesc: '开启后注入的工作流提示词使用中文（默认英文）。工具描述与错误消息由 GodotMCP server 提供，保持上游原文。',
  inherited: '继承默认值',
  overridden: '用户覆盖',
  save: '保存',
  saving: '保存中…',
  saveFailed: '保存失败：',
  saveTimeout: '保存超时：写入可能已生效，刷新页面后请核对开关状态；若反复出现请反馈。',
  saveNotApplied: '主程序未接受全部设置，已保留草稿。',
  pathTitle: '相关路径',
  pathDesc: 'server 入口为全局单值；项目路径按工作目录（cwd）批量条目，会话创建时按其工作目录匹配并把项目路径传给 server。两项都为空时该会话不挂载 MCP（CLI 解析链只决定 install 时写入 paths.json 的值）。',
  pathServerDist: 'Server 入口（全局）',
  pathServerDistPlaceholder: '.../server-dotnet/publish/<rid>/godot-mcp-shim.exe',
  pathProjects: '项目路径（按工作目录）',
  pathProjectsDesc: '项目路径不再在此配置：会话连哪个项目由模型调用 godot_connect(project) 决定，未指定时按会话工作目录下的 project.godot 或唯一活跃实例解析。此处的旧条目已失效。',
  pathAddProject: '添加',
  pathRemove: '删除',
  pathCwdPlaceholder: '工作目录 cwd',
  pathProjectPlaceholder: '.../project.godot',
  pathInvalid: 'cwd 与项目路径都必填且不能重复，非法条目不保存',
  pathEmpty: '尚未添加按工作目录的项目条目',
}

const en = {
  summary: 'MCP mounting policy, prompt, and related paths for GodotMCP.',
  loading: 'Reading settings…',
  unavailable: 'This deployment does not provide Godot settings.',
  readOnly: 'The settings document is read-only; changes cannot be saved.',
  injectLegacyMode: 'Legacy mode (global mount)',
  injectLegacyModeDesc: 'When on, one shared MCP connection is mounted on the host: every session sees the mcp__godot__* tools and the workflow prompt (project path from the global setting). Default off: only Godot-preset sessions mount the MCP; other sessions do not "hide" these tools — they have none.',
  promptGuidance: 'Workflow prompt',
  promptGuidanceDesc: 'Explain on-demand group activation, editor FIFO ordering, and error recovery in the system prompt. Applies only to sessions that mounted the Godot MCP (Godot preset or legacy mode).',
  toolCallTimeout: 'Tool call timeout (ms)',
  toolCallTimeoutDesc: 'Default 240000. Must exceed the server budget (210000), otherwise slow calls are cut off by the host before the server can explain the timeout. Changing it re-establishes the connection.',
  zhPrompt: 'Localize prompt',
  zhPromptDesc: 'When on, the injected workflow prompt uses Chinese (English by default). Tool descriptions and error messages come from the GodotMCP server and stay as provided.',
  inherited: 'Inherited',
  overridden: 'Overridden',
  save: 'Save',
  saving: 'Saving...',
  saveFailed: 'Save failed:',
  saveTimeout: 'Save timed out: the write may have landed; refresh and re-check the toggles. If it repeats, please report.',
  saveNotApplied: 'The host did not accept all settings; the draft was kept.',
  pathTitle: 'Related paths',
  pathDesc: 'The server entry is a single global value; project paths are batch entries keyed by workspace (cwd), matched when a session starts and passed to the server. When both are empty that session mounts nothing (the CLI resolve chain only decides what install writes into paths.json).',
  pathServerDist: 'Server entry (global)',
  pathServerDistPlaceholder: '.../server-dotnet/publish/<rid>/godot-mcp-shim.exe',
  pathProjects: 'Project paths (by workspace)',
  pathProjectsDesc: 'Project paths are no longer configured here: the session decides via godot_connect(project), falling back to project.godot in the session cwd or the sole active instance. Entries below no longer take effect.',
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
 * 页面「保存」按钮触发,经 /godot-config/api/path-settings 写 host。
 * 只保留添加行局部输入态与校验提示;每条目编辑/删除经回调上抛。 */
function PathSettingsSection(props: PathSectionProps): React.ReactElement {
  const t = props.t

  const sectionStyle = { marginTop: 10, borderTop: '1px solid var(--dsw-alias-border-l2)', paddingTop: 10 }
  const subTitleStyle = { fontSize: 13, fontWeight: 600, lineHeight: 1.5, marginBottom: 2 }
  const descStyle = { fontSize: 12, lineHeight: 1.5, color: 'var(--dsw-alias-label-tertiary)', marginBottom: 8 }

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

  return h('div', { style: sectionStyle, 'data-godot-paths': '' },
    h('div', { style: subTitleStyle }, t('pathTitle')),
    h('div', { style: descStyle }, t('pathDesc')),
    // server dist 全局单值。
    h('div', { style: { marginBottom: 10 } },
      h('label', { style: { display: 'block', fontSize: 12, lineHeight: 1.5, marginBottom: 2 } }, t('pathServerDist')),
      inputFor(props.serverDist, t('pathServerDistPlaceholder'), t('pathServerDist'), false, props.onServerDistChange),
    ),
    // 项目路径区:表已删除,只保留说明。
    //
    // 之前这里是一整套「添加 / 逐行编辑 / 删除」的交互。后端已经不再持久化
    // projects(GET 恒返回空数组、POST 忽略),留着交互会让用户加了几行、
    // 保存、刷新后全部消失 —— 比不显示更糟。说明文字保留,讲清该去哪配。
    h('div', { style: subTitleStyle }, t('pathProjects')),
    h('div', { style: descStyle }, t('pathProjectsDesc')),
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
  /** 单次工具调用超时（毫秒）。数值字段，与其他开关一起参与 diff 与保存。 */
  toolCallTimeoutMs: number
}

function normalized(value: unknown): SettingsValue {
  const source = typeof value === 'object' && value !== null ? value as Record<string, unknown> : {}
  const booleanOr = (key: keyof SettingsValue): boolean =>
    typeof source[key] === 'boolean' ? source[key] as boolean : DEFAULTS[key]
  // 超时接受数字或数字字符串（表单输入框给字符串），非法/非正数回退默认 ——
  // 与 injection.ts 的 resolveSettings 保持同一套判据。
  const timeoutOr = (): number => {
    const raw = source.toolCallTimeoutMs
    const parsed = typeof raw === 'number' ? raw : typeof raw === 'string' ? Number(raw) : Number.NaN
    return Number.isFinite(parsed) && parsed > 0 ? Math.round(parsed) : DEFAULTS.toolCallTimeoutMs
  }
  return {
    injectLegacyMode: booleanOr('injectLegacyMode'),
    promptGuidance: booleanOr('promptGuidance'),
    zhPrompt: booleanOr('zhPrompt'),
    toolCallTimeoutMs: timeoutOr(),
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
        // 相关路径:只提交 serverDist。项目路径表已删除,后端也会忽略 projects,
        // 这里直接发空数组 —— 不发"注定被丢弃"的数据,免得日后有人以为它在生效。
        const sDist = serverDist.trim()
        await api.savePathSettings(sDist, [])
        setSavedServerDist(sDist)
        setSavedProjects([])
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
              // 单次工具调用超时(毫秒)。三层预算阶梯里宿主那一层:
              // daemon 长工具 180s < shim 210s < 宿主。改它会触发重挂。
              h('div', { style: { marginTop: 8, marginBottom: 8 } },
                h('label', { style: { display: 'block', fontSize: 12, lineHeight: 1.5, marginBottom: 2 } },
                  t('toolCallTimeout')),
                h('input', {
                  type: 'number',
                  min: 1000,
                  step: 1000,
                  value: String(draft.toolCallTimeoutMs),
                  disabled: !editable,
                  'aria-label': t('toolCallTimeout'),
                  spellCheck: false,
                  style: uiStyles.input,
                  onChange: function (event: { target: { value: string } }) {
                    const parsed = Number(event.target.value)
                    // 非法输入不写进草稿:留着会让"保存"按钮永远灰着而用户不知道原因。
                    if (Number.isFinite(parsed) && parsed > 0) change('toolCallTimeoutMs', Math.round(parsed))
                  },
                }),
                h('div', { style: { fontSize: 12, lineHeight: 1.5, color: 'var(--dsw-alias-label-tertiary)' } },
                  t('toolCallTimeoutDesc')),
              ),
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
  // DSH 0.1.7 起 settingsScope 退役:经 configForms 按入口 id(profile 行 id,
  // 与旧 settings 命名空间 'godot' 不同名)取 ConfigForm;snapshot/subscribe/
  // set 面与旧 settingsScope 同构。缺失时跳过(页面留空,不抛错)。
  const configForms = ctx.configForms
  if (configForms === undefined || configForms === null || typeof configForms.get !== 'function') return
  const scope = configForms.get(CONFIG_ENTRY_ID)
  const t = ctx.locale.bind(LOCALE_NAMESPACE)
  const ConfigPage = createForm(scope, t)
  ctx.effect(function () {
    return ctx.locale.register(LOCALE_NAMESPACE, DICTS)
  }, 'godot config: settings dictionaries')
  ctx.slots.inject('plugins.bundle.config', function () {
    return ctx.slots.register({
      name: 'plugins.bundle.config',
      key: BUNDLE_PACKAGE_NAME,
      locale: LOCALE_NAMESPACE,
    }, ConfigPage)
  })
}
