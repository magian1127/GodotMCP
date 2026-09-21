@tool
extends RefCounted
## 秘密清洗器(scrubber)— 在通过上下文协议(MCP)传输层返回之前,从日志/错误输出中
## 抹除凭据。应用于 log_read 的两个通道;绝不应用于源码读取
## (源码不应被改动)。

# 三条高精度模式,以最小的误报覆盖真实秘密。刻意省略了宽泛的十六进制
# 模式([A-Fa-f0-9]{32,})— 它在 Godot UID、资源哈希和导入校验和上的
# 误报,超过了它在秘密检测上的边际收益。
# 只有发现具体泄漏时才在此处添加模式。
const PATTERNS: Array[String] = [
	"(?i)(api[_-]?key|token|secret|password)\\s*[:=]\\s*[\\w\\.\\-]+",
	"sk-[A-Za-z0-9]{20,}",
	"(?i)bearer\\s+[A-Za-z0-9\\.\\-_]+",
]


## 在脚本加载时编译一次。
static var _compiled_patterns: Array[RegEx] = _compile_patterns()

static func _compile_patterns() -> Array[RegEx]:
	var out: Array[RegEx] = []
	for pattern in PATTERNS:
		var regex := RegEx.new()
		regex.compile(pattern)
		out.append(regex)
	return out


## 从文本中清洗秘密。返回 {text: String, redaction_count: int}。
## 当 redaction_count > 0 且 source 非空时,发出一条 push_warning,
## 便于开发者将 [REDACTED] 的出现与清洗器关联起来。
static func scrub(text: String, source: String = "") -> Dictionary:
	var out := text
	var redaction_count := 0
	for regex in _compiled_patterns:
		var matches := regex.search_all(out)
		redaction_count += matches.size()
		out = regex.sub(out, "[REDACTED]", true)
	if redaction_count > 0 and not source.is_empty():
		push_warning("[Scrubber] %d redactions applied to %s" % [redaction_count, source])
	return {"text": out, "redaction_count": redaction_count}
