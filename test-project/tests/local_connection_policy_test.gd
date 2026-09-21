extends SceneTree

const MCPJsonSync := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_sync.gd")
const MCPJsonDiscovery := preload(
	"res://addons/godot_mcp_toolkit/ui/mcp_json_discovery.gd")
const ExtensionCatalog := preload(
	"res://addons/godot_mcp_toolkit/ui/dock/ext/extension_catalog.gd")

var _failures := 0


func _init() -> void:
	_test_server_entry_builder()
	_test_bundled_template()
	_test_project_config()
	_test_legacy_server_key()
	_test_local_extension_catalog()
	if _failures == 0:
		print("All local connection policy tests passed.")
	quit(0 if _failures == 0 else 1)


func _test_server_entry_builder() -> void:
	var entry := MCPJsonSync.build_server_entry()
	_check(entry.get("type", "") == "http", "entry is not daemon HTTP type")
	_check(str(entry.get("url", "")).begins_with("http://127.0.0.1:"), "entry does not point at the loopback daemon")
	_check("npx" not in JSON.stringify(entry).to_lower(), "entry emitted a package runner")
	_check(MCPJsonSync.can_write_mcp_json(), "daemon HTTP entry has no local prerequisite but write was refused")


func _test_bundled_template() -> void:
	var path := "res://addons/godot_mcp_toolkit/.mcp.json.template"
	var text := FileAccess.get_file_as_string(path)
	_check(not text.is_empty(), "bundled .mcp.json template is missing")
	_check("\"godot\":" in text, "template does not use the integrated server key")
	_check("\"type\": \"http\"" in text, "template does not select the daemon HTTP face")
	_check("npx" not in text.to_lower(), "template still contains a package runner")
	_check(("npgame" + "dev") not in text.to_lower(), "template still names an upstream package")


func _test_project_config() -> void:
	var path := ProjectSettings.globalize_path("res://") + ".mcp.json"
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	_check(parsed is Dictionary, "test-project .mcp.json is not valid JSON")
	if not parsed is Dictionary:
		return
	var servers: Dictionary = parsed.get("mcpServers", {})
	var entry: Dictionary = servers.get("godot", {})
	_check(entry.get("type", "") == "http", "test-project does not point at the daemon HTTP face")
	var url := str(entry.get("url", ""))
	_check(url.begins_with("http://127.0.0.1:"), "test-project entry is not a loopback daemon URL")


func _test_legacy_server_key() -> void:
	# 2026-10 键简化:godot 为正式键,godot-mcp-unified 仅读取兼容。
	var entry: Dictionary = MCPJsonDiscovery.server_entry(_document_with_key("godot"))
	_check(entry.get("type", "") == "http", "new server key was not read")
	var legacy: Dictionary = MCPJsonDiscovery.server_entry(_document_with_key("godot-mcp-unified"))
	_check(legacy.get("type", "") == "http", "legacy server key lost read compatibility")


func _document_with_key(server_key: String) -> Dictionary:
	return {"mcpServers": {server_key: {"type": "http", "url": "http://127.0.0.1:6590/"}}}


func _test_local_extension_catalog() -> void:
	var parsed := ExtensionCatalog.read_catalog()
	_check(bool(parsed.get("ok", false)), "bundled local extension catalog is invalid")
	_check(ExtensionCatalog.is_allowed_local_path(
		"res://addons/godot_mcp_toolkit/extensions"), "local extension root was rejected")
	_check(ExtensionCatalog.is_allowed_local_path(
		"res://addons/godot_mcp_toolkit/extensions/example"), "local extension child was rejected")
	_check(not ExtensionCatalog.is_allowed_local_path(
		"res://addons/godot_mcp_toolkit/extensions/../docs"), "traversal path was accepted")
	_check(not ExtensionCatalog.is_allowed_local_path(
		"remote" + "://extension"), "remote extension path was accepted")


func _check(condition: bool, message: String) -> void:
	if condition:
		return
	_failures += 1
	push_error("FAIL: " + message)
