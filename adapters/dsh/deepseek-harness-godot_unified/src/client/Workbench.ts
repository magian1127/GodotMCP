// 工作台主组件:状态条 + 三栏编排 + 确认弹窗;不经对话,全部走 /godot-workbench/api。
// 路径设置区已迁移到插件页配置表单(plugins.bundle.config,DSH 0.1.6+);
// 本组件不再承载路径设置,只保留工具树/调用/结果/技能/确认弹窗。
import React from 'react'
import { api, type CallOutcome, type PathSettings, type StatusPayload, type ToolsPayload } from './api.js'
import type { ToolEntry } from './grouping.js'
import { loadHistory, pushHistory, saveHistory, summarize, type HistoryItem } from './history.js'
import { ToolTree } from './ToolTree.js'
import { CallPanel } from './CallPanel.js'
import { ResultPanel } from './ResultPanel.js'
import { SkillsPanel } from './SkillsPanel.js'
import { detectLang, STRINGS } from './locales.js'

const h = React.createElement

/** conversation.view 标准 props 暴露的会话数据(SessionSummary.cwd 即工作目录)。 */
interface GodotWorkbenchProps {
  sessionId?: string
  useSessions?: (selector: (state: unknown) => unknown) => unknown
}

/** 渲染异常防御:任一子组件崩溃时降级为错误占位,而不是把整树卸载成白屏。
 * 触发源历史:工作台路径设置区对某些输入的渲染可抛异常,导致 React 整树卸载、
 * 工作表内容消失。即使该触发源已随路径区迁移离开本组件,仍保留边界兜底。 */
class WorkbenchErrorBoundary extends React.Component<{ children?: React.ReactNode }, { hasError: boolean; message: string }> {
  state = { hasError: false, message: '' }

  static getDerivedStateFromError(error: unknown): { hasError: boolean; message: string } {
    return { hasError: true, message: error instanceof Error ? error.message : String(error) }
  }

  componentDidCatch(error: unknown, info: unknown): void {
    console.error('[godot-workbench] 渲染崩溃,已降级:', error, info)
  }

  render(): React.ReactNode {
    if (this.state.hasError) {
      return h('div', { className: 'gwb-err', style: { padding: 16, fontSize: 13 } },
        h('div', { style: { fontWeight: 600, marginBottom: 6 } }, '工作台渲染异常'),
        h('p', { style: { opacity: 0.75, margin: '0 0 10px' } }, '某个面板渲染失败,已降级为占位以避免整树空白(错误多数不致命,可刷新重试)。'),
        h('pre', { className: 'gwb-pre' }, this.state.message),
      )
    }
    return this.props.children ?? null
  }
}

export function GodotWorkbench(props: GodotWorkbenchProps = {}): React.ReactElement {
  const lang = detectLang()
  const t = (key: string): string => STRINGS[lang][key] ?? key
  // 当前会话工作目录(status 用 cwd 解析生效路径);framework 未注入 useSessions 时缺省为空。
  const sessionId = props.sessionId
  const useSessions = props.useSessions
  const cwd = useSessions !== undefined
    ? (useSessions((state: unknown) => {
        const byId = (state as { byId?: Record<string, { cwd?: string }> } | undefined)?.byId
        return sessionId === undefined ? undefined : byId?.[sessionId]?.cwd
      }) as string | undefined)
    : undefined

  const [status, setStatus] = React.useState<StatusPayload | null>(null)
  const [tools, setTools] = React.useState<ToolsPayload | null>(null)
  const [pathInfo, setPathInfo] = React.useState<PathSettings | null>(null)
  const [statusError, setStatusError] = React.useState<string | null>(null)
  const [toolsError, setToolsError] = React.useState<string | null>(null)
  const [busy, setBusy] = React.useState(true)
  // 「连接 Godot」:无缓存/需刷新时显式拉起桥接(连接成功后工具清单缓存,零闲置开销)。
  const [connecting, setConnecting] = React.useState(false)
  const [connectError, setConnectError] = React.useState<string | null>(null)
  // 「Godot 项目」就地编辑(按当前 cwd 写回 paths.json;空值=清除该 cwd 条目)。
  const [pathEditOpen, setPathEditOpen] = React.useState(false)
  const [pathDraft, setPathDraft] = React.useState('')
  const [pathSaving, setPathSaving] = React.useState(false)
  const [pathError, setPathError] = React.useState<string | null>(null)
  const [search, setSearch] = React.useState('')
  const [selected, setSelected] = React.useState<ToolEntry | null>(null)
  const [prefill, setPrefill] = React.useState('')
  const [running, setRunning] = React.useState<{ callId: string; name: string } | null>(null)
  const [result, setResult] = React.useState<CallOutcome | null>(null)
  const [history, setHistory] = React.useState<HistoryItem[]>([])
  const [rightTab, setRightTab] = React.useState<'run' | 'skills'>('run')
  const [skipConfirm, setSkipConfirm] = React.useState(false)
  const [confirming, setConfirming] = React.useState<{ name: string; args: unknown } | null>(null)

  const errText = (error: unknown): string => error instanceof Error ? error.message : String(error)
  // status/tools 并行拉取:任一路失败都记录原因并清空对应数据,状态条据此
  // 从"加载中"转为可诊断的错误徽标 + 重试按钮(MCP/编辑器就绪后一键重连探测)。
  // pathSettings(cwd) 同批带回当前工作目录的生效项目路径(整份 projects 供就地编辑)。
  const refresh = React.useCallback((): void => {
    setBusy(true)
    void Promise.allSettled([api.status(cwd), api.tools(), api.pathSettings(cwd)])
      .then(([s, tl, p]) => {
        if (s.status === 'fulfilled') { setStatus(s.value); setStatusError(null) }
        else { setStatus(null); setStatusError(errText(s.reason)) }
        if (tl.status === 'fulfilled') { setTools(tl.value); setToolsError(null) }
        else { setTools(null); setToolsError(errText(tl.reason)) }
        if (p.status === 'fulfilled') { setPathInfo(p.value) }
        else { setPathInfo(null) }
        setBusy(false)
      })
  }, [cwd])
  React.useEffect(() => {
    setHistory(loadHistory(window.localStorage))
    refresh()
  }, [refresh])

  const connectGodot = (): void => {
    setConnecting(true)
    setConnectError(null)
    void api.connect(cwd)
      .then(outcome => {
        if (!outcome.ok) setConnectError(outcome.error?.message ?? t('connectFail'))
        refresh() // 连接成功后工具面可能变化/出现。
      })
      .catch(error => { setConnectError(errText(error)) })
      .finally(() => { setConnecting(false) })
  }

  const runCall = (name: string, args: unknown): void => {
    const cid = 'gwbc-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8)
    setRunning({ callId: cid, name })
    void api.call(name, args, cid)
      .then(outcome => {
        setResult(outcome)
        setHistory(prev => {
          const next = pushHistory(prev, { id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, name, argsJson: JSON.stringify(args), ok: !outcome.isError, durationMs: outcome.durationMs, at: Date.now(), summary: summarize(outcome.content) })
          saveHistory(window.localStorage, next)
          return next
        })
        refresh() // 组激活类调用可能改变工具面。
      })
      .catch(error => {
        setResult({ callId: '', name, isError: true, durationMs: 0, content: [], error: error instanceof Error ? error.message : String(error) })
      })
      .finally(() => { setRunning(null) })
  }

  const godotPrefix = `${status?.bridge.serverName ?? 'godot'}_`
  // 就地保存当前 cwd 对应的 Godot 项目路径:读整份 projects → 替换/追加当前 cwd 条目(空=清除) → 整份写回。
  const saveProjectPath = async (): Promise<void> => {
    if (pathInfo === null || cwd === undefined || cwd === '') return
    const trimmed = pathDraft.trim()
    const others = pathInfo.projects.filter(p => p.cwd !== cwd)
    const next = trimmed === '' ? others : [...others, { cwd, projectPath: trimmed }]
    setPathSaving(true)
    setPathError(null)
    try {
      const saved = await api.savePathSettings(pathInfo.serverDist, next)
      setPathInfo({ ...pathInfo, serverDist: saved.serverDist, projects: saved.projects, currentCwd: cwd, currentProjectPath: trimmed === '' ? null : trimmed })
      setPathEditOpen(false)
      refresh() // status 按新生效路径重读。
    } catch (error) {
      setPathError(errText(error))
    } finally {
      setPathSaving(false)
    }
  }
  const unsafeGroupTools = new Set((tools?.godotGroups ?? []).filter(g => g.active && /unsafe/i.test(g.name)).flatMap(g => g.tools.map(name => `${godotPrefix}${name}`)))
  const isUnsafeCall = (name: string, args: unknown): boolean => /unsafe/i.test(name) || unsafeGroupTools.has(name) || /unsafe/i.test(JSON.stringify(args))
  const requestRun = (name: string, args: unknown): void => {
    if (!skipConfirm || isUnsafeCall(name, args)) setConfirming({ name, args })
    else runCall(name, args)
  }
  const onExecute = (args: unknown): void => { if (selected !== null) requestRun(selected.name, args) }

  // data-conversation-composer-overlay 与 ui-trajectory 的 TrajectoryView 同款标记:
  // ConversationRoot.module.css 的 :has() 规则据此把本视图切成 overlay 布局——
  // scroll 体不再滚动、view 全高自持滚动器、底部 composer 变为浮动条并为内容
  // 预留 --dsh-composer-height 空间(与轨迹 Tab 一致,不显示对话式粘底输入框)。
  return h('div', { className: 'gwb', 'data-conversation-composer-overlay': '' },
    h('div', { className: 'gwb-bar' },
      status === null && statusError === null
        ? h('span', { className: 'gwb-badge warn' }, t('loading'))
        : statusError !== null
          ? h('span', { className: 'gwb-badge warn', title: statusError }, `${t('statusError')}: ${statusError}`)
          : h(React.Fragment, null,
              h('span', { className: 'gwb-badge' }, `${t('bridge')} ${status?.bridge.toolCount ?? 0} ${t('toolsCount')}`),
              status?.editor === null || status?.editor?.port === null
                ? h('span', { className: 'gwb-badge warn' }, `${t('editor')}: ${t('notRunning')}`)
                : h('span', { className: `gwb-badge ${status?.editor?.listening ? 'ok' : 'warn'}` },
                    `${t('editor')}: ${status?.editor?.project.replace(/\\/g, '/').split('/').pop()} · ${status?.editor?.port} · ${status?.editor?.godotVersion ?? '?'}${status?.editor?.listening ? '' : ' · ' + t('notRunning')}`),
              status?.readOnly && h('span', { className: 'gwb-badge warn' }, t('readOnly')),
              status?.unsafe && h('span', { className: 'gwb-badge warn' }, 'UNSAFE'),
              status?.serverDist.path !== null && status?.serverDist.path !== undefined && !status?.serverDist.exists && h('span', { className: 'gwb-badge warn' }, `${t('serverDist')} ${t('missing')}`),
              status?.serverDist.path === null && h('span', { className: 'gwb-badge warn' }, t('installHint')),
              toolsError !== null && h('span', { className: 'gwb-badge warn', title: toolsError }, t('toolsError')),
              connectError !== null && h('span', { className: 'gwb-badge warn', title: connectError }, `${t('connectFail')}: ${connectError}`),
            ),
      // 「连接 Godot」仅在未连上时显示:工具清单已加载(bridge.toolCount>0)即视为已连接
      // (桥接已建连并同步过工具清单),此时不再需要显式连接入口;需要重拉时用「刷新」。
      (status === null || status.bridge.toolCount === 0) && h('button', {
        className: 'gwb-btn primary',
        style: { marginLeft: 'auto' },
        disabled: busy || connecting,
        title: t('connectHint'),
        onClick: connectGodot,
      }, connecting ? t('connecting') : t('connectGodot')),
      h('button', {
        className: 'gwb-btn' + (statusError !== null ? ' primary' : ''),
        style: { marginLeft: 'auto' },
        disabled: busy,
        onClick: refresh,
      }, busy ? t('retrying') : (statusError !== null ? t('retry') : t('refresh'))),
    ),
    // 当前会话工作目录与生效 Godot 项目路径(按 cwd 查 paths.json);可就地修改写回。
    h('div', { className: 'gwb-path', style: { display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', padding: '5px 10px', fontSize: 12, borderBottom: '1px solid var(--dsw-alias-border-l2)' } },
      h('span', { style: { opacity: 0.85 } }, `${t('pathCwd')}: ${cwd ?? '—'}`),
      h('span', { style: { opacity: 0.85, wordBreak: 'break-all' } },
        `${t('pathProject')}: `,
        pathInfo?.currentProjectPath !== null && pathInfo?.currentProjectPath !== undefined && pathInfo.currentProjectPath !== ''
          ? pathInfo.currentProjectPath
          : t('pathProjectNone')),
      cwd !== undefined && cwd !== '' && h('button', {
        className: 'gwb-btn', disabled: pathSaving,
        onClick: () => { setPathDraft(pathInfo?.currentProjectPath ?? ''); setPathEditOpen(!pathEditOpen); setPathError(null) },
      }, pathEditOpen ? t('cancel') : t('pathEdit')),
      pathEditOpen && h('span', { className: 'gwb-path-edit', style: { display: 'inline-flex', gap: 6, alignItems: 'center', flex: '1 1 240px', minWidth: 0 } },
        h('input', {
          className: 'gwb-search', style: { flex: '1 1 200px', minWidth: 120 },
          value: pathDraft, placeholder: t('pathProjectPlaceholder'), spellCheck: false,
          onInput: (e: React.FormEvent<HTMLInputElement>) => { setPathDraft(e.currentTarget.value) },
        }),
        h('button', { className: 'gwb-btn' + (pathSaving ? '' : ' primary'), disabled: pathSaving, onClick: () => { void saveProjectPath() } },
          pathSaving ? t('pathSaving') : t('save')),
      ),
      pathError !== null && h('span', { className: 'gwb-badge warn', title: pathError }, `${t('pathSaveFail')}: ${pathError}`),
    ),
    h('div', { className: 'gwb-main' },
      h('div', { className: 'gwb-col' },
        h('div', { className: 'gwb-col-head' }, t('godot'), ` · ${tools?.tools.length ?? 0}`),
        h(ToolTree, {
          tools: (tools?.tools ?? []).map(x => ({ name: x.name, description: x.description ?? '', parameters: x.parameters })),
          godotGroups: tools?.godotGroups ?? [], godotPrefix, selectedName: selected?.name ?? null, search, t,
          onSearch: setSearch,
          onSelect: (tool) => { setSelected(tool); setPrefill('') },
          onActivateGroup: (groupName) => {
            const meta = (tools?.tools ?? []).find(x => x.name === `${godotPrefix}discover_tools`)
            if (meta === undefined) return
            setSelected({ name: meta.name, description: meta.description ?? '', parameters: meta.parameters })
            setPrefill(groupName)
          },
        }),
      ),
      h('div', { className: 'gwb-col' },
        h('div', { className: 'gwb-col-head' }, selected?.name ?? t('noTool')),
        h(CallPanel, { tool: selected, prefill, running: running !== null, runningName: running?.name ?? null, t, onExecute, onCancel: () => { if (running !== null && running.callId !== '') void api.cancel(running.callId) } }),
      ),
      h('div', { className: 'gwb-col' },
        h('div', { className: 'gwb-col-head' },
          h('button', { className: 'gwb-tab' + (rightTab === 'run' ? ' on' : ''), onClick: () => { setRightTab('run') } }, t('result')),
          h('button', { className: 'gwb-tab' + (rightTab === 'skills' ? ' on' : ''), onClick: () => { setRightTab('skills') } }, t('skills')),
        ),
        rightTab === 'run'
          ? h(ResultPanel, {
              result, history, t, replayDisabled: running !== null,
              onReplay: (item) => {
                const tool = (tools?.tools ?? []).find(x => x.name === item.name)
                if (tool === undefined) return
                setSelected({ name: tool.name, description: tool.description ?? '', parameters: tool.parameters })
                setPrefill('')
                try { requestRun(item.name, JSON.parse(item.argsJson)) } catch { /* 历史参数损坏:忽略 */ }
              },
              onClearHistory: () => { setHistory([]); saveHistory(window.localStorage, []) },
            })
          : h(SkillsPanel, { lang, t }),
      ),
    ),
    confirming !== null && h('div', { className: 'gwb-modal', onClick: () => { setConfirming(null) } },
      h('div', { className: 'gwb-modal-box', onClick: (e: React.MouseEvent) => { e.stopPropagation() } },
        h('div', { style: { fontWeight: 600, marginBottom: 8 } }, t('confirmTitle')),
        h('div', { style: { fontSize: 12, marginBottom: 6 } }, t('confirmBody')),
        h('pre', { className: 'gwb-pre' }, `${confirming.name}\n${JSON.stringify(confirming.args, null, 2)}`),
        h('div', { style: { display: 'flex', gap: 8, marginTop: 10, alignItems: 'center' } },
          h('button', { className: 'gwb-btn primary', onClick: () => { const c = confirming; setConfirming(null); runCall(c.name, c.args) } }, t('confirmRun')),
          h('button', { className: 'gwb-btn', onClick: () => { setConfirming(null) } }, t('close')),
          h('label', { style: { marginLeft: 'auto', fontSize: 12 } },
            h('input', { type: 'checkbox', checked: skipConfirm, onChange: (e: React.FormEvent<HTMLInputElement>) => { setSkipConfirm(e.currentTarget.checked) } }), ' ', t('skipConfirm')),
        ),
      ),
    ),
  )
}

/** 受 ErrorBoundary 保护的工作台根组件:注册进 conversation.view 时用它,确保任何
 * 子面板渲染崩溃都不致整树白屏。 */
export function GodotWorkbenchRoot(props: GodotWorkbenchProps = {}): React.ReactElement {
  return h(WorkbenchErrorBoundary, null, h(GodotWorkbench, props))
}
