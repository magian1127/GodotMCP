// 调用历史(localStorage,上限 50;摘要 8KB 截断)。
export interface HistoryItem { id: string; name: string; argsJson: string; ok: boolean; durationMs: number; at: number; summary: string }
export interface HistoryStorage { getItem(key: string): string | null; setItem(key: string, value: string): void }

const KEY = 'dsh-godot-workbench-history'
const MAX_ITEMS = 50
const SUMMARY_MAX = 8 * 1024

export function loadHistory(storage: HistoryStorage): HistoryItem[] {
  try {
    const parsed = JSON.parse(storage.getItem(KEY) ?? '[]') as HistoryItem[]
    return Array.isArray(parsed) ? parsed.slice(0, MAX_ITEMS) : []
  } catch { return [] }
}

export function saveHistory(storage: HistoryStorage, items: HistoryItem[]): void {
  try { storage.setItem(KEY, JSON.stringify(items.slice(0, MAX_ITEMS))) } catch { /* 配额满:放弃持久化 */ }
}

export function pushHistory(items: HistoryItem[], item: HistoryItem): HistoryItem[] {
  return [item, ...items].slice(0, MAX_ITEMS)
}

export function summarize(content: Array<{ type: string; text?: string }>): string {
  const text = content.filter(b => b.type === 'text').map(b => b.text ?? '').join('\n')
  return text.length > SUMMARY_MAX ? text.slice(0, SUMMARY_MAX) + '…(截断)' : text
}
