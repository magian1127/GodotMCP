[英文原文](multi-instance.md)

# 多实例与 MCP 会话

## 连接当前会话

宿主使用 stdio shim，提供会话 UUID 和 cwd；daemon 从机器级注册表发现编辑器。先 godot_connect：显式 project/instance 优先，省略时依次用本键旧目标、cwd 本层 project.godot、唯一活跃实例，不递归扫描。

```text
godot_connect(project="<absolute Godot project path>", groups=["scene_edit"])
list_instances()
scene_get_tree()
discover_tools(request=["code_intel", "editor_ops"], include_schemas=true)
```

未连接只有入口，连接后是 9 个核心工具加本键激活的组，内置组共 17 个。以宿主实际名称为准，DSH 加 mcp__godot__；只有入口原名带 godot_ 前缀。

普通调用按显式 instance、本键默认目标、唯一活跃实例寻址。键隔离组和目标，不隔离权限；共享一个 shim 的会话也共享目标和组。

## 支持的组合

| 方式 | 行为 |
| --- | --- |
| 不同目录/worktree | 支持；注册表键、token、端点独立 |
| 一个编辑器，多个游戏进程 | 部分支持；只发现最后注册的运行时，调试桥跟踪最近游戏 |
| 两个编辑器，同一目录 | 不支持；注册表键/token 路径和 Godot 元数据冲突 |

多人联机使用不同 worktree。编辑器默认扫描 6550–6560，运行时扫描 6570–6585，并发布真实端口。daemon 读取注册表端点，不使用 GODOT_MCP_PROJECT_PATH 选项目。

同 application/config/name 的副本可能共享文件日志。Godot 4.5+ 优先 log_read source="buffer"，旧版可能回退文件。不同应用名可隔离用户数据和日志目录。

## 端口与 LSP

编辑器/运行时端口变量配置监听进程。当前 C# daemon 发现注册表端点，不读取已退役 Node 服务的端口/项目覆盖。无效或占用的固定值明确失败，不悄悄扫描；见 [高级配置](advanced_configuration.zh-CN.md)。

LSP 独立于 WS，通常为 6005；DAP 通常为 6006，应避开。并行编辑器需要不同的实际监听端口，且发布的 EditorSettings network/language_server/remote_host、remote_port 与实际值相符。

addon 当前发布编辑器设置，不知道已被引擎消费的 --lsp-port 覆盖，daemon 读取注册表字段；单传命令行端口可能留下错误的注册值。历史 GODOT_MCP_LSP_PORT/HOST 指南适用于已退役 Node 桥；当前 C# 的错误提示即使仍提及它们，也没有读取覆盖。确认实际监听与发布一致，做不到时一次只验收一个编辑器的 LSP。

Godot 4.5+ 才有跨项目工作区根校验；旧版冲突检测降级，TCP 已连接不等于项目身份正确。

## 恢复与当前限制

- connect/激活组后确认宿主刷新；TTL=0 本身不是证据。shim 已支持 legacy 内部通知泵及重连，现代宿主自有 listen 的重放仍未完成。
- 重启后核对目标与组；最近 connect/discover 帧不能恢复任意增量历史。新 shim 有新 UUID 和空状态。
- 用 discover_tools(reset=[...]) 停用并核对列表；当前 connect 替换组后，一次浏览可能复活旧桶。
- include_schemas=true 对部分常驻组工具仅返回名称；激活后从 tools/list 读完整 schema。
- 多项目交错传 instance；同键切目标与复合调用顺序完成，整个调用入口固定一次目标尚未实现。
- 缺头应修正 stdio/旧 HTTP 配置；TARGET_NOT_RUNNING 应启动所报编辑器。
- 尊重 unsafe/只读门控，超时重提变更之前先读状态。

完整运行限制归插件 server-dotnet README。[配套技能](companion-skills.zh-CN.md) 提供创作流程。
