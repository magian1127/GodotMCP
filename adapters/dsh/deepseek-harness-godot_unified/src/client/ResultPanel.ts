// 结果区:结果/历史两个子面板。
import React from 'react'
import type { CallOutcome } from './api.js'
import type { HistoryItem } from './history.js'

const h = React.createElement

export interface ResultPanelProps {
  result: CallOutcome | null
  history: HistoryItem[]
  t: (key: string) => string
  onReplay: (item: HistoryItem) => void
  replayDisabled: boolean
  onClearHistory: () => void
}

function pretty(text: string | undefined): string {
  if (text === undefined) return ''
  const trimmed = text.trim()
  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    try { return JSON.stringify(JSON.parse(trimmed), null, 2) } catch { return text }
  }
  return text
}

export function ResultPanel(props: ResultPanelProps): React.ReactElement {
  const [tab, setTab] = React.useState<'result' | 'history'>('result')
  const [copied, setCopied] = React.useState(false)
  const [copyError, setCopyError] = React.useState(false)
  React.useEffect(() => { setTab('result') }, [props.result])
  const r = props.result

  /** 复制文本:Clipboard API 优先,失败/不可用时降级 textarea + execCommand;失败给反馈。 */
  const copyText = (text: string): void => {
    setCopyError(false)
    const ok = (): void => { setCopied(true); setTimeout(() => { setCopied(false) }, 1500) }
    const fallback = (): void => {
      try {
        const ta = document.createElement('textarea')
        ta.value = text
        ta.setAttribute('readonly', '')
        ta.style.position = 'fixed'
        ta.style.opacity = '0'
        document.body.appendChild(ta)
        ta.select()
        const succeeded = document.execCommand('copy')
        document.body.removeChild(ta)
        if (succeeded) ok()
        else { setCopyError(true); setTimeout(() => { setCopyError(false) }, 2000) }
      } catch {
        setCopyError(true)
        setTimeout(() => { setCopyError(false) }, 2000)
      }
    }
    if (typeof navigator !== 'undefined' && navigator.clipboard !== undefined && typeof navigator.clipboard.writeText === 'function') {
      navigator.clipboard.writeText(text).then(ok).catch(fallback)
    } else {
      fallback()
    }
  }

  return h('div', { style: { display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 } },
    h('div', { className: 'gwb-tabs' },
      h('button', { className: 'gwb-tab' + (tab === 'result' ? ' on' : ''), onClick: () => { setTab('result') } }, props.t('result')),
      h('button', { className: 'gwb-tab' + (tab === 'history' ? ' on' : ''), onClick: () => { setTab('history') } }, `${props.t('history')} (${props.history.length})`),
    ),
    h('div', { className: 'gwb-body' },
      tab === 'history'
        ? h('div', { className: 'gwb-hist' },
            h('button', { className: 'gwb-btn', style: { alignSelf: 'flex-start' }, onClick: props.onClearHistory }, props.t('clear')),
            props.history.length === 0 && h('div', { className: 'gwb-empty' }, props.t('noResult')),
            props.history.map(item => h('div', { key: item.id, className: 'gwb-hist-row' },
              h('span', { className: 'gwb-hist-name', title: item.name }, item.name),
              h('span', { className: 'gwb-hist-sum', title: item.summary }, item.ok ? '✓' : '✕', ' ', item.summary.slice(0, 80)),
              h('span', null, `${item.durationMs}ms`),
              h('button', { className: 'gwb-btn', disabled: props.replayDisabled, onClick: () => { props.onReplay(item) } }, props.t('replay')),
            )),
          )
        : r === null
          ? h('div', { className: 'gwb-empty' }, props.t('noResult'))
          : h('div', { className: r.isError ? 'gwb-err' : undefined },
              h('div', { style: { display: 'flex', gap: 8, alignItems: 'center', marginBottom: 6, fontSize: 12, flexWrap: 'wrap' } },
                h('strong', null, r.name),
                h('span', null, `${props.t('durationMs')} ${r.durationMs}ms`),
                r.error !== undefined && h('span', { style: { color: 'var(--dsw-alias-danger,#c62828)', wordBreak: 'break-all' } }, r.error),
                copyError && h('span', { style: { color: 'var(--dsw-alias-danger,#c62828)' } }, props.t('copyFail')),
                h('button', { className: 'gwb-btn', style: { marginLeft: 'auto' }, onClick: () => {
                  const text = r.content.map(b => b.text ?? (b.type === 'image' ? `[${b.mimeType} ${b.data?.length ?? 0}B${b.truncated === true ? ' ' + props.t('truncated') : ''}]` : `[${b.summary ?? b.type}]`)).join('\n')
                  copyText(text)
                } }, copied ? props.t('copied') : props.t('copy')),
              ),
              r.content.map((block, index) => {
                if (block.type === 'image') {
                  return h(React.Fragment, { key: index },
                    h('img', { className: 'gwb-img', src: `data:${block.mimeType ?? 'image/png'};base64,${block.data ?? ''}`, alt: 'result' }),
                    block.truncated === true && h('div', { style: { fontSize: 11, opacity: 0.7 } }, props.t('truncated')),
                  )
                }
                if (block.type === 'other') return h('div', { key: index, style: { opacity: 0.7, fontSize: 12 } }, block.summary ?? block.type)
                return h('pre', { key: index, className: 'gwb-pre' }, pretty(block.text))
              }),
            ),
    ),
  )
}
