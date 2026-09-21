@tool
extends RefCounted
## 不可信内容信封(envelope)包装器。
##
## 在返回给 LLM 之前,把用户创作/项目创作的内容包装进带随机数标签
## (nonce)的 <untrusted-{nonce}> 信封中。会先从正文中清洗掉已有的
## 信封标签,以防止标签逃逸(tag-breakout)注入。绝不应用于写入路径
## 或二进制(截图)数据。

## 在脚本加载时编译一次 — 每个编辑器会话只分配一次。
static var _envelope_tag_re: RegEx = _compile_envelope_re()

static func _compile_envelope_re() -> RegEx:
	var re := RegEx.new()
	re.compile("(?i)<\\s*/?\\s*untrusted(?:-[0-9a-f]*)?(?:\\s[^>]*)?>")
	return re


static func wrap(kind: String, source: String, body: String) -> String:
	var nonce := "%08x" % randi()
	var scrubbed := _scrub_envelope_tags(body)
	return '<untrusted-%s kind="%s" source="%s">\n%s\n</untrusted-%s>' % [nonce, kind, source, scrubbed, nonce]


static func _scrub_envelope_tags(text: String) -> String:
	return _envelope_tag_re.sub(text, "[scrubbed-envelope-tag]", true)
