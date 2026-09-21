// 双语文案(语言经 document.documentElement.lang 检测,零模块依赖)。
export type Lang = 'zh' | 'en'

export function detectLang(): Lang {
  return typeof document !== 'undefined' && document.documentElement.lang.toLowerCase().startsWith('zh') ? 'zh' : 'en'
}

export const STRINGS: Record<Lang, Record<string, string>> = {
  zh: {
    search: '搜索工具…', godot: 'Godot', godotGroups: 'Godot·已激活组', builtin: '内置工具', plugin: '其他插件',
    inactiveGroups: '按需工具组(未激活)', activateHint: '选中元工具并预填组名,执行即搜索/激活',
    execute: '执行', cancel: '取消', rawJson: '原始 JSON', formMode: '表单',
    confirmTitle: '确认执行工具', confirmBody: '即将执行工具调用,请确认参数:', confirmRun: '确认执行',
    skipConfirm: '本会话跳过确认', result: '结果', history: '历史', skills: '技能', sections: '提示词面',
    clear: '清空', replay: '重放', copy: '复制', copied: '已复制', copyFail: '复制失败', durationMs: '耗时',
    refresh: '刷新', retry: '重试', retrying: '重试中…', bridge: '桥接', editor: '编辑器', notRunning: '未运行', serverDist: 'server',
    missing: '缺失', readOnly: '只读模式', loading: '加载中…', noTool: '从左侧选择一个工具',
    statusError: '状态获取失败', toolsError: '工具列表获取失败',
    noResult: '尚无调用结果', installHint: '工作台未配置:运行 dsh-godot install 写入配置后刷新',
    truncated: '图片过大已截断', toolsCount: '个工具', close: '关闭',
    groupTools: '组内工具', promptRefresh: '重新拉取',
    pathCwd: '当前工作目录', pathProject: 'Godot 项目', pathProjectNone: '未配置',
    pathEdit: '修改', pathSaving: '保存中…', pathSaveFail: '保存失败', save: '保存',
    pathProjectPlaceholder: 'Godot 项目目录（含 project.godot 的文件夹，不是文件）',
    connectGodot: '连接 Godot', connecting: '连接中…', connectFail: '连接失败', connectHint: '连接后获取并缓存工具清单(有缓存时零连接)',
  },
  en: {
    search: 'Search tools…', godot: 'Godot', godotGroups: 'Godot·active groups', builtin: 'Built-in', plugin: 'Other plugins',
    inactiveGroups: 'On-demand groups (inactive)', activateHint: 'Selects the meta tool prefilled with the group name; run it to search/activate',
    execute: 'Run', cancel: 'Cancel', rawJson: 'Raw JSON', formMode: 'Form',
    confirmTitle: 'Confirm tool run', confirmBody: 'A tool call is about to run. Review the arguments:', confirmRun: 'Run anyway',
    skipConfirm: 'Skip confirms this session', result: 'Result', history: 'History', skills: 'Skills', sections: 'Prompt sections',
    clear: 'Clear', replay: 'Replay', copy: 'Copy', copied: 'Copied', copyFail: 'Copy failed', durationMs: 'Duration',
    refresh: 'Refresh', retry: 'Retry', retrying: 'Retrying…', bridge: 'Bridge', editor: 'Editor', notRunning: 'not running', serverDist: 'server',
    missing: 'missing', readOnly: 'read-only', loading: 'Loading…', noTool: 'Pick a tool on the left',
    statusError: 'Failed to load status', toolsError: 'Failed to load tools',
    noResult: 'No call yet', installHint: 'Workbench unconfigured: run dsh-godot install, then refresh',
    truncated: 'image truncated (too large)', toolsCount: 'tools', close: 'Close',
    groupTools: 'group tools', promptRefresh: 'Reload',
    pathCwd: 'Workspace cwd', pathProject: 'Godot project', pathProjectNone: 'not set',
    pathEdit: 'Edit', pathSaving: 'Saving…', pathSaveFail: 'Save failed', save: 'Save',
    pathProjectPlaceholder: 'Godot project directory (the folder containing project.godot, not the file)',
    connectGodot: 'Connect Godot', connecting: 'Connecting…', connectFail: 'Connect failed', connectHint: 'Fetches and caches the tool inventory after connecting (zero cost when cached)',
  },
}
