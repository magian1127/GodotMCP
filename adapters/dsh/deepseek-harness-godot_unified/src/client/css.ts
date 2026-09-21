// 工作台样式(主题 token + 容器查询;返回清理函数移除 style 节点)。
export function injectCss(): () => void {
  if (typeof document === 'undefined') return () => {}
  const style = document.createElement('style')
  style.setAttribute('data-godot-workbench', '1')
  style.textContent = [
    '.gwb{display:flex;flex-direction:column;height:100%;min-height:0;color:var(--dsw-alias-fg-default,inherit);font-size:13px;background:var(--dsw-alias-bg-layer-1,inherit)}',
    '.gwb-bar{display:flex;gap:10px;align-items:center;flex-wrap:wrap;padding:6px 10px;border-bottom:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));font-size:12px}',
    '.gwb-badge{padding:1px 6px;border-radius:8px;border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35))}',
    '.gwb-badge.ok{color:var(--dsw-alias-success,#2e7d32)}',
    '.gwb-badge.warn{color:var(--dsw-alias-warning,#b26a00)}',
    // 底部让位:data-conversation-composer-overlay 模式下 composer 是浮动条
    // (DSh ConversationRoot 发布 --dsh-composer-height),轨迹视图用同式
    // --dsh-trajectory-bottom-clearance 避免表格底行被浮动输入框遮挡。
    '.gwb-main{display:grid;grid-template-columns:minmax(180px,240px) minmax(280px,1fr) minmax(300px,1.2fr);gap:8px;padding:8px;padding-bottom:calc(var(--dsh-composer-height,152px) + 16px);flex:1;min-height:0;container-type:inline-size}',
    '.gwb-col{display:flex;flex-direction:column;min-height:0;border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));border-radius:6px;overflow:hidden}',
    '.gwb-col-head{padding:6px 8px;font-weight:600;border-bottom:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));display:flex;gap:6px;align-items:center;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.gwb-tree{flex:1;overflow:auto;padding:4px}',
    '.gwb-search{width:100%;padding:4px 6px;margin-bottom:4px;box-sizing:border-box;border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));border-radius:4px;background:transparent;color:inherit}',
    '.gwb-group{margin:6px 0 2px;font-size:12px;opacity:.85}',
    '.gwb-item{display:block;width:100%;text-align:left;padding:4px 6px;border:0;background:none;color:inherit;border-radius:4px;cursor:pointer;font-size:12px;line-height:1.35}',
    '.gwb-item:hover{background:rgba(128,128,128,.12)}',
    '.gwb-item.on{background:rgba(128,128,128,.2);font-weight:600}',
    '.gwb-item small{display:block;opacity:.7;font-weight:400;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.gwb-call{flex:1;overflow:auto;padding:8px;display:flex;flex-direction:column;gap:8px}',
    '.gwb-field{display:grid;grid-template-columns:110px 1fr;gap:6px;align-items:start}',
    '.gwb-field label{font-size:12px;padding-top:4px}',
    '.gwb-field input,.gwb-field select,.gwb-field textarea{width:100%;box-sizing:border-box;padding:4px 6px;border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));border-radius:4px;background:transparent;color:inherit;font:inherit}',
    '.gwb-field textarea{min-height:52px;font-family:monospace}',
    '.gwb-actions{display:flex;gap:8px;align-items:center;margin-top:auto;padding-top:8px}',
    '.gwb-btn{padding:4px 12px;border-radius:4px;border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));background:transparent;color:inherit;cursor:pointer;font:inherit}',
    '.gwb-btn.primary{background:var(--dsw-alias-accent,#478cbf);color:#fff;border-color:transparent}',
    '.gwb-btn:disabled{opacity:.5;cursor:default}',
    '.gwb-tabs{display:flex;gap:2px;padding:4px 6px 0;border-bottom:1px solid var(--dsw-alias-border,rgba(128,128,128,.35))}',
    '.gwb-tab{padding:4px 10px;border:0;border-bottom:2px solid transparent;background:none;color:inherit;cursor:pointer;font:inherit;font-size:12px}',
    '.gwb-tab.on{border-bottom-color:var(--dsw-alias-accent,#478cbf);font-weight:600}',
    '.gwb-body{flex:1;overflow:auto;padding:8px}',
    '.gwb-pre{white-space:pre-wrap;word-break:break-word;font-family:monospace;font-size:12px;margin:0;padding:6px;background:rgba(128,128,128,.08);border-radius:4px}',
    '.gwb-err .gwb-pre{color:var(--dsw-alias-danger,#c62828)}',
    '.gwb-img{max-width:100%;border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));border-radius:4px;display:block;margin:6px 0}',
    '.gwb-hist{display:flex;flex-direction:column;gap:4px}',
    '.gwb-hist-row{display:flex;gap:6px;align-items:center;font-size:12px;padding:4px;border-radius:4px;border:1px solid transparent}',
    '.gwb-hist-row:hover{border-color:var(--dsw-alias-border,rgba(128,128,128,.35))}',
    '.gwb-hist-name{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:40%}',
    '.gwb-hist-sum{opacity:.7;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1}',
    '.gwb-empty{opacity:.6;padding:16px;text-align:center}',
    // 遮罩走宿主官方 token(--dsw-alias-bg-mask-1 浅色≈rgba(0,0,0,.24),暗色主题自适配),
    // 不再用固定的 rgba(0,0,0,.45)(过于黑);blur 与官方 Modal 一致。
    '.gwb-modal{position:fixed;inset:0;background:var(--dsw-alias-bg-mask-1,rgba(0,0,0,.24));backdrop-filter:var(--dsw-mask-blur,none);display:flex;align-items:center;justify-content:center;z-index:9999}',
    '.gwb-modal-box{max-width:520px;width:90%;background:var(--dsw-alias-bg-elevated,inherit);border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));border-radius:8px;padding:14px}',
    '.gwb-skill{margin-bottom:8px;border:1px solid var(--dsw-alias-border,rgba(128,128,128,.35));border-radius:6px}',
    '.gwb-skill>button{width:100%;text-align:left;padding:6px 8px;border:0;background:none;color:inherit;cursor:pointer;font:inherit;font-weight:600}',
    '.gwb-skill-body{padding:0 10px 10px;white-space:pre-wrap;font-size:12px;max-height:50vh;overflow:auto}',
    // 路径设置区已迁移到插件页配置表单(plugins.bundle.config,DSH 0.1.6+);本工作台不再包含 gwb-paths 区,
    // 但保留 .gwb-err 供 ErrorBoundary 降级占位使用。
    '.gwb-err{color:var(--dsw-alias-danger,#c62828)}',
    '@container (max-width: 900px){.gwb-main{grid-template-columns:1fr;grid-template-rows:auto auto 1fr;overflow:auto}}',
    '@container (max-width: 560px){.gwb-field{grid-template-columns:1fr}.gwb-bar{font-size:11px}}',
  ].join('\n')
  document.head.appendChild(style)
  return () => { style.remove() }
}
