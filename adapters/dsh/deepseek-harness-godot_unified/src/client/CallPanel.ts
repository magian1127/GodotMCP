// 调用区:表单/原始 JSON 双模式 + 执行/取消;确认弹窗由父组件持有。
import React from 'react'
import { buildArguments, formFields, type FieldSpec } from './form-schema.js'
import type { ToolEntry } from './grouping.js'

const h = React.createElement

export interface CallPanelProps {
  tool: ToolEntry | null
  prefill: string
  running: boolean
  runningName: string | null
  t: (key: string) => string
  onExecute: (args: unknown) => void
  onCancel: () => void
}

export function CallPanel(props: CallPanelProps): React.ReactElement {
  const [rawMode, setRawMode] = React.useState(false)
  const [values, setValues] = React.useState<Record<string, string | boolean>>({})
  const [raw, setRaw] = React.useState('{}')
  const [errors, setErrors] = React.useState<string[]>([])
  const fields: FieldSpec[] = React.useMemo(() => formFields(props.tool?.parameters), [props.tool?.name, props.tool?.parameters])
  // 选择或预填变化 → 重置表单(渲染期同步重置,React 官方认可的 lastKey 模式)。
  const resetKey = `${props.tool?.name ?? ''}|${props.prefill}`
  const [lastKey, setLastKey] = React.useState(resetKey)
  if (lastKey !== resetKey) {
    setLastKey(resetKey)
    const next: Record<string, string | boolean> = {}
    const firstInput = fields.find(f => f.type === 'string' || f.type === 'enum')
    for (const field of fields) {
      if (props.prefill !== '' && firstInput !== undefined && field.name === firstInput.name) next[field.name] = props.prefill
      else if (field.type === 'json') next[field.name] = field.placeholder ?? '{}'
    }
    setValues(next)
    setRaw(props.prefill !== '' && firstInput !== undefined
      ? JSON.stringify({ [firstInput.name]: props.prefill }, null, 2)
      : '{}')
    setErrors([])
  }
  if (props.tool === null) return h('div', { className: 'gwb-empty' }, props.t('noTool'))
  const run = (): void => {
    if (rawMode) {
      try { props.onExecute(JSON.parse(raw)); setErrors([]) } catch (error) { setErrors([`JSON 解析失败: ${error instanceof Error ? error.message : String(error)}`]) }
      return
    }
    const built = buildArguments(fields, values)
    setErrors(built.errors)
    if (built.errors.length === 0) props.onExecute(built.args)
  }
  return h('div', { className: 'gwb-call' },
    h('div', null,
      h('div', { style: { fontWeight: 600, wordBreak: 'break-all' } }, props.tool.name),
      h('div', { style: { fontSize: 12, opacity: 0.75, whiteSpace: 'pre-wrap' } }, props.tool.description),
    ),
    h('div', { style: { display: 'flex', gap: 6 } },
      h('button', { className: 'gwb-tab' + (rawMode ? '' : ' on'), onClick: () => { setRawMode(false) } }, props.t('formMode')),
      h('button', { className: 'gwb-tab' + (rawMode ? ' on' : ''), onClick: () => { setRawMode(true) } }, props.t('rawJson')),
    ),
    rawMode
      ? h('textarea', { style: { width: '100%', minHeight: 160, boxSizing: 'border-box', fontFamily: 'monospace' }, value: raw, onInput: (e: React.FormEvent<HTMLTextAreaElement>) => { setRaw(e.currentTarget.value) } })
      : fields.map(field => h('div', { key: field.name, className: 'gwb-field' },
          h('label', null, field.name + (field.required ? ' *' : '')),
          field.type === 'boolean'
            ? h('input', { type: 'checkbox', checked: values[field.name] === true, onChange: (e: React.FormEvent<HTMLInputElement>) => { setValues({ ...values, [field.name]: e.currentTarget.checked }) } })
            : field.type === 'enum'
              ? h('select', { value: String(values[field.name] ?? ''), onChange: (e: React.FormEvent<HTMLSelectElement>) => { setValues({ ...values, [field.name]: e.currentTarget.value }) } },
                  h('option', { value: '' }, '—'),
                  (field.enumValues ?? []).map(v => h('option', { key: v, value: v }, v)))
              : field.type === 'json'
                ? h('textarea', { value: String(values[field.name] ?? field.placeholder ?? '{}'), onInput: (e: React.FormEvent<HTMLTextAreaElement>) => { setValues({ ...values, [field.name]: e.currentTarget.value }) } })
                : h('input', { type: field.type === 'number' ? 'number' : 'text', value: String(values[field.name] ?? ''), placeholder: field.description ?? '', onInput: (e: React.FormEvent<HTMLInputElement>) => { setValues({ ...values, [field.name]: e.currentTarget.value }) } }),
        )),
    fields.length === 0 && !rawMode && h('div', { className: 'gwb-empty' }, props.tool.parameters === undefined ? '(无参数 schema,用原始 JSON 模式)' : '(无参数)'),
    errors.length > 0 && h('div', { className: 'gwb-err' }, h('pre', { className: 'gwb-pre' }, errors.join('\n'))),
    h('div', { className: 'gwb-actions' },
      props.running
        ? h('button', { className: 'gwb-btn', onClick: props.onCancel }, `${props.t('cancel')} ${props.runningName ?? ''}`)
        : h('button', { className: 'gwb-btn primary', onClick: run }, props.t('execute')),
    ),
  )
}
