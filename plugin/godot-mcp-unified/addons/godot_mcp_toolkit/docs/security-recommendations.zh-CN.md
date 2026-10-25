[英文原文](security-recommendations.md)

# 安全建议

## 当前实现的边界

宿主使用 stdio shim，loopback HTTP 是有认证的后端。shim 读稳定 daemon token，daemon 读编辑器 token；凭据留在机器级注册表。会话键隔离目标/组，不隔离权限；初始组和隐藏工具不能代替授权。

桥接监听仅在本地；工具输入输出是否发送给模型服务，由宿主数据策略决定。

## 工具能力

| 工具 | 能力 |
| --- | --- |
| execute_code、node_call_method | 任意代码/方法执行，需 daemon 显式启用 unsafe |
| project_delete | 删除项目文件/场景/目录，先检查 dry_run 计划 |
| save_write、save_delete | user:// 持久写入/删除 |
| user_data_read | 读取用户文件和目录 |
| 其它创作工具 | 修改场景/资源/设置，行为由注解描述 |

readOnlyHint、destructiveHint、idempotentHint 为宿主策略描述操作，不是身份验证。部分变更支持 Undo，但应验证实际结果，不能默认可回滚。

## 只读配置

当前 C# daemon **没有实现已退役 Node 桥的 GODOT_MCP_READ_ONLY 环境过滤**。addon UI 仍有该键的兼容配置/状态，这不能证明变更已被拦截。不要把 dock 开关或该 env 当作已经强制执行的只读边界。

使用宿主实际的允许列表、拒绝规则和审批策略。Codex 独立 MCP 服务器可配置在受信任项目的 .codex/config.toml 或用户级配置：

```toml
[mcp_servers.godot]
command = "<plugin root>/server-dotnet/publish/<rid>/godot-mcp-shim.exe"
disabled_tools = ["execute_code", "node_call_method"]
```

该例只阻止两个工具，不代表全部写入被禁止。限制工作流时可用明确的 enabled_tools，保留 godot_connect 入口和所需读取工具。已安装插件的服务器使用插件级 MCP 策略，而不是改写其传输命令。见 本机 Codex 的 MCP 配置说明；CLI 命令可用 codex mcp --help 查看。

其它宿主配置和命名不同。使用当前版本的过滤/权限机制，并验证真实可见性或调用拒绝；Agents SDK 的 Python tool_filter 不是 Codex 桌面端配置。

## 使用原则

- 先连接再按任务激活组。缺工具可能是未连接、未加载组、版本门控或目录陈旧。
- 只有当前已授权操作需要时才启用 unsafe。
- 保留路径守卫、审计、响应上限、不可信内容包装；这些服务端检查与宿主发现是不同层。
- 超时/传输中断后先读状态，再决定是否重提变更。
- 文件系统和模型访问由宿主权限限制，MCP 工具限制不会取消原生文件读取能力。
- 通知与组恢复的当前边界见 [会话恢复](multi-instance.zh-CN.md)。
