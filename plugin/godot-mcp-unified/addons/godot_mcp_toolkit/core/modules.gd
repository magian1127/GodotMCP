@tool
extends RefCounted
## 插件内部脚本的集中预加载。
##
## 脚本路径只存在于这一处 —— 如果某个文件移动了,只需更新本文件,其他一概不动。
## MCPToolkitError 拥有 class_name,无需预加载。

const Coerce := preload("res://addons/godot_mcp_toolkit/contract/coerce.gd")
const ExecuteHints := preload("res://addons/godot_mcp_toolkit/contract/execute_hints.gd")
const Pagination := preload("res://addons/godot_mcp_toolkit/contract/pagination.gd")
const ScreenshotResponse := preload("res://addons/godot_mcp_toolkit/contract/screenshot_response.gd")
const FileGuard := preload("res://addons/godot_mcp_toolkit/security/file_guard.gd")
const Untrusted := preload("res://addons/godot_mcp_toolkit/security/untrusted.gd")
const Scrubber := preload("res://addons/godot_mcp_toolkit/security/scrubber.gd")
const Audit := preload("res://addons/godot_mcp_toolkit/security/audit.gd")
const MCPJsonSync := preload("res://addons/godot_mcp_toolkit/ui/mcp_json_sync.gd")
const RegistryClient := preload("res://addons/godot_mcp_toolkit/registry/registry_client.gd")
const ProjectPaths := preload("res://addons/godot_mcp_toolkit/paths/project_paths.gd")
const UserPathMonitor := preload("res://addons/godot_mcp_toolkit/paths/user_path_monitor.gd")
const LogBuffer := preload("res://addons/godot_mcp_toolkit/logging/log_buffer.gd")
const CommandHelpers := preload("res://addons/godot_mcp_toolkit/commands/editor_helpers.gd")
const LogHelpers := preload("res://addons/godot_mcp_toolkit/logging/log_helpers.gd")
const NodejsCheck := preload("res://addons/godot_mcp_toolkit/versioning/nodejs_check.gd")
const VersionUtils := preload("res://addons/godot_mcp_toolkit/versioning/mcp_version_utils.gd")
const StaleInstanceHint := preload("res://addons/godot_mcp_toolkit/versioning/stale_instance_hint.gd")
const EditorAccess := preload("res://addons/godot_mcp_toolkit/core/editor_access.gd")
const DaemonSidecar := preload("res://addons/godot_mcp_toolkit/daemon/daemon_sidecar.gd")
