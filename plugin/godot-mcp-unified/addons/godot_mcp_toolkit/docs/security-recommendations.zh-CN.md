[英文原文](security-recommendations.md)

# 安全建议

本文档说明哪些 MCP 工具具有风险、如何使用 AI 智能体内置的过滤功能限制它们，以及如何为受监督环境启用 Toolkit 的只读模式。

## 高风险工具

根据你的信任模型，下面六个工具值得考虑按工具单独阻止。前两个工具还要求 MCP 服务器以 `GODOT_MCP_UNSAFE=1` 启动，否则不会出现在可发现工具中：

| 工具 | 风险 | 可能阻止它的原因 | 阻止后的副作用 |
|---|---|---|---|
| `execute_code` | 通过 Expression 执行任意 GDScript | 防止智能体在运行时执行不受约束的代码 | 智能体无法动态测试游戏逻辑，也无法通过表达式检查运行时状态 |
| `node_call_method` | 调用任意场景节点上的任意方法 | 防止智能体调用未记录或危险的方法 | 智能体无法调用节点上的自定义方法——必须改用专用工具 |
| `project_delete` | 删除项目文件、文件夹、场景、脚本或资源 | 防止破坏性项目清理 | 智能体必须保留过时的项目路径 |
| `save_write` | 写入 `user://` 持久化存储 | 防止智能体修改玩家存档 / 配置 | 智能体无法创建或更新存档文件、配置和偏好设置 |
| `save_delete` | 从 `user://` 删除内容 | 防止存档数据意外丢失 | 智能体无法清理测试存档或过时配置 |
| `user_data_read` | 读取 `user://` 文件或列出其中目录 | 防止访问可能敏感的玩家数据和文件结构 | 智能体无法通过 MCP 检查存档（若未受沙箱限制，仍可使用原生文件系统访问） |

任何带有 `destructiveHint` MCP 注释的工具都会修改项目资源或状态。其中许多工具支持 Undo（在编辑器中按 Ctrl+Z 可撤销更改）。

## 内置只读模式

如果希望会话只读，又不想逐个配置工具阻止，`GODOT_MCP_READ_ONLY=1` 是最简单的选项。将它设置在 `.mcp.json` 的 `env` 中，所有标注 `destructiveHint` 的工具都会对智能体隐藏——这涵盖任意代码执行（`execute_code`）、节点方法调用（`node_call_method`）、用户文件夹写入（`save_write`、`save_delete`）以及其他所有变更工具。

**它不会阻止什么：**`user_data_read` 等只读工具仍然可见。智能体仍可检查存档内容和目录结构（如果智能体未受沙箱限制，也可以通过自己的原生文件系统访问读取这些路径）。

```jsonc
// .mcp.json -- env block
{
  "env": {
    "GODOT_MCP_READ_ONLY": "1"
  }
}
```

如需更细粒度的控制（例如允许变更但只阻止 `project_delete`），请使用下面的按工具智能体侧过滤。除非明确需要任意表达式和方法执行，否则不要设置 `GODOT_MCP_UNSAFE`。

## 按工具在智能体侧阻止

### Claude Code

`.claude/settings.json` 权限系统：

```json
{
  "permissions": {
    "deny": [
      "mcp__godot__execute_code",
      "mcp__godot__node_call_method"
    ]
  }
}
```

格式：`mcp__<server-name>__<tool-name>`。支持通配符：`mcp__godot__save_*` 会阻止写入和删除工具；若读取也敏感，请另行阻止 `mcp__godot__user_data_read`。拒绝规则在所有层级都优先于允许规则。

### Google Gemini CLI

`~/.gemini/config.json`（或项目级配置）：

```json
{
  "mcpServers": {
    "godot-mcp": {
      "excludeTools": [
        "execute_code",
        "node_call_method",
        "project_delete",
        "save_write",
        "save_delete"
      ]
    }
  }
}
```

`excludeTools` 始终优先于 `includeTools`。

### OpenAI Codex / Agents SDK

Python 代码，在服务器初始化时设置工具过滤器：

```python
from agents.mcp import MCPServerStdio, create_static_tool_filter

server = MCPServerStdio(
    params={"command": "godot-mcp-unified-server"},
    tool_filter=create_static_tool_filter(
        blocked_tool_names=["execute_code", "node_call_method"]
    ),
)
```

同时支持 `allowed_tool_names`（允许列表）和 `blocked_tool_names`（阻止列表），也支持异步、带上下文的过滤函数。

### Cursor

截至 2026-05-27，Cursor 支持通过 **Hooks** 进行 MCP 工具过滤（拦截工具调用并返回 allow/deny/warn 的程序化脚本）。这比 Claude Code 或 Gemini 的声明式配置需要更多设置。Cursor 还提供 `permissions.json` 自动运行允许列表（控制审批流程，而不是阻止）。目前还没有简单的声明式 `excludeTools` 风格配置。请查看 Cursor 的最新文档，因为这一领域仍在快速演进。

### 通用建议

对于其他兼容 MCP 的智能体，请查阅其工具过滤文档。寻找 allowlist/blocklist/disallow/exclude 机制。MCP 协议会为每个工具公开 `destructiveHint` 和 `readOnlyHint` 注释——行为良好的客户端应在执行破坏性操作前请求确认。
