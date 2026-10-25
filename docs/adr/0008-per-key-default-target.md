# 0008: 每键默认目标——修订实例寻址

中文 | [English](0008-per-key-default-target.en.md)

## 决策

自有会话键允许保存默认目标而不依赖 MCP 协议会话。需要目标的工具按显式 instance（规范化路径或 12 位短 id）、本键默认目标、唯一活跃实例的顺序寻址，否则报错并列清单。全局只读工具有各自参数语义，见运行文档。

godot_connect 写目标；空参先用本键旧目标，再检查 cwd 本层 project.godot，最后使用唯一活跃实例。显式 project 当前未运行仍可保存，后续普通调用报 TARGET_NOT_RUNNING 并给启动路径提示。daemon 不启动编辑器，不递归扫描项目。

解析服务 SessionTargetResolver 与 GroupService 独立，交给 InstanceManager 的统一寻址，避免构造环。标识仍是路径或短 id，不新增 connection_id。版本可见性仍取实例能力并集，不因默认目标缩窄。

## 当前实现边界

“目标在 tools/call 入口解析一次并钉住”是原设计要求，**尚未完成**；当前每个子调用重新读取默认目标，同键切换可使复合操作跨项目。在完成前，同一会话顺序切换目标，多项目交错工作显式传 instance。

groups 省略时保留现组，显式 groups 的设计语义是替换；当前 GroupService 旧桶未同步清空，有浏览复活问题。调整现有组用 discover reset，并核对实际列表。重启恢复也不是完整的最终状态恢复，详见 [daemon README](../../plugin/godot-mcp-unified/server-dotnet/README.md)。

## 取舍

继续要求每次传 instance 易漏参；只有唯一候选时才用默认目标没有额外价值。新哈希句柄不可逆，也无法对离线目标解析，因此沿用路径。此 ADR 修订 [0003](0003-instance-addressing.md) 中“没有自有会话状态”的前提，不改变运行时随编辑器实例寻址的规则。
