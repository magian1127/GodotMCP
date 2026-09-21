@tool
extends RefCounted
## 为确定性的试玩测试(playtest)控制一个有界的运行中游戏时钟。
##
## 每次单步(step)结束时场景树总是保持冻结。[method thaw] 会恢复本控制器
## 首次冻结场景树之前存在的暂停状态。

const Coerce := preload("res://addons/godot_mcp_toolkit/contract/coerce.gd")

const MAX_FRAMES := 3600
const MAX_EXPRESSION_LENGTH := 2048
const MAX_REPORT_EXPRESSIONS := 16

var _controlled := false
var _restore_paused := false
var _stepping := false
var _total_frames_advanced := 0


func control(tree: SceneTree, parameters: Dictionary) -> Dictionary:
	var action := str(parameters.get("action", "status"))
	match action:
		"freeze":
			return _freeze(tree)
		"thaw":
			return _thaw(tree)
		"status":
			return _status(tree)
		"step":
			return await _step(tree, parameters, false)
		"step_until":
			return await _step(tree, parameters, true)
		_:
			return _failure(
				"INVALID_PARAMS",
				"action must be freeze, thaw, status, step, or step_until",
			)


func _freeze(tree: SceneTree) -> Dictionary:
	if _stepping:
		return _failure("INVALID_PARAMS", "cannot freeze while a step is in progress")
	var already_controlled := _controlled
	_engage(tree)
	var result := _status(tree)
	result["already_controlled"] = already_controlled
	return result


func _thaw(tree: SceneTree) -> Dictionary:
	if _stepping:
		return _failure("INVALID_PARAMS", "cannot thaw while a step is in progress")
	var was_controlled := _controlled
	if _controlled:
		tree.paused = _restore_paused
		_controlled = false
	var result := _status(tree)
	result["was_controlled"] = was_controlled
	return result


func _status(tree: SceneTree) -> Dictionary:
	return {
		"success": true,
		"controlled": _controlled,
		"stepping": _stepping,
		"tree_paused": tree.paused,
		"restore_paused": _restore_paused if _controlled else null,
		"total_frames_advanced": _total_frames_advanced,
		"physics_ticks_per_second": Engine.physics_ticks_per_second,
	}


func _step(tree: SceneTree, parameters: Dictionary, stop_on_predicate: bool) -> Dictionary:
	if _stepping:
		return _failure("INVALID_PARAMS", "another step is already in progress")

	var frame_budget_result := _resolve_frame_budget(parameters, stop_on_predicate)
	if not frame_budget_result["success"]:
		return frame_budget_result
	var frame_budget: int = frame_budget_result["frames"]

	var predicate: Expression = null
	if stop_on_predicate:
		var predicate_result := _compile_expression(str(parameters.get("until", "")), "until")
		if not predicate_result["success"]:
			return predicate_result
		predicate = predicate_result["expression"]

	var report_result := _compile_reports(parameters.get("report", []))
	if not report_result["success"]:
		return report_result
	var reports: Array = report_result["reports"]

	_engage(tree)
	var initial_predicate := false
	if predicate != null:
		var initial_result := _evaluate(predicate, tree)
		if not initial_result["success"]:
			return initial_result
		initial_predicate = bool(initial_result["value"])

	var started_at := Time.get_ticks_msec()
	var frames_advanced := 0
	var predicate_met := initial_predicate
	_stepping = true
	while frames_advanced < frame_budget and not predicate_met:
		# 代理(agent)拥有的单步控制优先于有界窗口期间发生的暂停写入。
		tree.paused = false
		await tree.process_frame
		frames_advanced += 1
		if predicate != null:
			var predicate_result := _evaluate(predicate, tree)
			if not predicate_result["success"]:
				tree.paused = true
				_stepping = false
				return predicate_result
			predicate_met = bool(predicate_result["value"])

	tree.paused = true
	_stepping = false
	_total_frames_advanced += frames_advanced

	var readings_result := _evaluate_reports(reports, tree)
	if not readings_result["success"]:
		return readings_result

	return {
		"success": true,
		"controlled": true,
		"tree_paused": true,
		"frames_requested": frame_budget,
		"frames_advanced": frames_advanced,
		"predicate_met": predicate_met if stop_on_predicate else null,
		"elapsed_wall_ms": Time.get_ticks_msec() - started_at,
		"report": readings_result["values"],
		"hint": "The game is frozen at the stop frame. Inspect state, inject input, then step again or thaw.",
	}


func _resolve_frame_budget(parameters: Dictionary, stop_on_predicate: bool) -> Dictionary:
	var frames := int(parameters.get("frames", 0))
	var duration_ms := int(parameters.get("duration_ms", 0))
	if stop_on_predicate:
		frames = int(parameters.get("max_frames", frames if frames > 0 else 600))
	elif frames <= 0 and duration_ms > 0:
		frames = int(ceil(duration_ms * Engine.physics_ticks_per_second / 1000.0))
	if frames <= 0:
		return _failure(
			"INVALID_PARAMS",
			"step requires frames or duration_ms; step_until requires a positive max_frames",
		)
	if frames > MAX_FRAMES:
		return _failure(
			"INVALID_PARAMS",
			"frame budget exceeds the %d-frame safety cap" % MAX_FRAMES,
		)
	return {"success": true, "frames": frames}


func _compile_expression(source: String, label: String) -> Dictionary:
	if source.is_empty():
		return _failure("INVALID_PARAMS", "%s expression must not be empty" % label)
	if source.length() > MAX_EXPRESSION_LENGTH:
		return _failure(
			"INVALID_PARAMS",
			"%s expression exceeds %d characters" % [label, MAX_EXPRESSION_LENGTH],
		)
	var expression := Expression.new()
	var parse_error := expression.parse(source, ["tree", "root"])
	if parse_error != OK:
		return _failure(
			"PARSE_ERROR",
			"%s expression could not be parsed: %s" % [label, expression.get_error_text()],
		)
	return {"success": true, "expression": expression, "source": source}


func _compile_reports(raw_reports: Variant) -> Dictionary:
	if typeof(raw_reports) != TYPE_ARRAY:
		return _failure("INVALID_PARAMS", "report must be an array of expressions")
	var sources: Array = raw_reports
	if sources.size() > MAX_REPORT_EXPRESSIONS:
		return _failure(
			"INVALID_PARAMS",
			"report accepts at most %d expressions" % MAX_REPORT_EXPRESSIONS,
		)
	var reports: Array = []
	for index in sources.size():
		if typeof(sources[index]) != TYPE_STRING:
			return _failure("INVALID_PARAMS", "report expression %d must be a string" % index)
		var compiled := _compile_expression(str(sources[index]), "report[%d]" % index)
		if not compiled["success"]:
			return compiled
		reports.append(compiled)
	return {"success": true, "reports": reports}


func _evaluate(expression: Expression, tree: SceneTree) -> Dictionary:
	var value = expression.execute([tree, tree.current_scene], self, false)
	if expression.has_execute_failed():
		return _failure("EXECUTE_FAILED", expression.get_error_text())
	return {"success": true, "value": value}


func _evaluate_reports(reports: Array, tree: SceneTree) -> Dictionary:
	var values := {}
	for report in reports:
		var evaluated := _evaluate(report["expression"], tree)
		if not evaluated["success"]:
			return evaluated
		values[report["source"]] = Coerce.serialize_value(evaluated["value"])
	return {"success": true, "values": values}


func _engage(tree: SceneTree) -> void:
	if not _controlled:
		_restore_paused = tree.paused
		_controlled = true
	tree.paused = true


func _failure(code: String, message: String) -> Dictionary:
	return {
		"success": false,
		"code": code,
		"error": message,
	}
