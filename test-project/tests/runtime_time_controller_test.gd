extends SceneTree

const PlaytestTimeController := preload(
	"res://addons/godot_mcp_toolkit/runtime/playtest_time_controller.gd"
)

var _failures := 0


class FrameCounter:
	extends Node

	var frames := 0


	func _process(_delta: float) -> void:
		frames += 1


func _initialize() -> void:
	call_deferred("_run")


func _run() -> void:
	var controller: RefCounted = PlaytestTimeController.new()
	var counter := FrameCounter.new()
	counter.name = "FrameCounter"
	root.add_child(counter)
	await process_frame

	var status: Dictionary = controller.control(self, {"action": "status"})
	_check(status["success"] and not status["controlled"], "initial status is uncontrolled")

	var frozen: Dictionary = controller.control(self, {"action": "freeze"})
	_check(frozen["success"] and paused, "freeze pauses the tree")
	var before_step := counter.frames

	var stepped: Dictionary = await controller.control(
		self,
		{
			"action": "step",
			"frames": 3,
			"report": ["tree.root.get_node('FrameCounter').frames"],
		},
	)
	_check(stepped["success"], "step succeeds")
	_check(int(stepped["frames_advanced"]) == 3, "step advances exactly three frames")
	_check(counter.frames - before_step == 3, "pausable game node runs exactly three frames")
	_check(paused, "step leaves the tree frozen")
	_check(stepped["report"].size() == 1, "step returns stop-frame report values")

	var target := counter.frames + 2
	var stepped_until: Dictionary = await controller.control(
		self,
		{
			"action": "step_until",
			"until": "tree.root.get_node('FrameCounter').frames >= %d" % target,
			"max_frames": 10,
		},
	)
	_check(stepped_until["success"], "step_until succeeds")
	_check(stepped_until["predicate_met"], "step_until stops when the predicate becomes true")
	_check(int(stepped_until["frames_advanced"]) == 2, "step_until stops on the matching frame")

	var invalid: Dictionary = await controller.control(
		self,
		{"action": "step", "frames": 3601},
	)
	_check(not invalid["success"] and invalid["code"] == "INVALID_PARAMS", "frame cap rejects unbounded steps")

	var thawed: Dictionary = controller.control(self, {"action": "thaw"})
	_check(thawed["success"] and not paused, "thaw restores the original unpaused state")

	paused = true
	controller.control(self, {"action": "freeze"})
	await controller.control(self, {"action": "step", "frames": 1})
	controller.control(self, {"action": "thaw"})
	_check(paused, "thaw preserves a game pause that existed before agent control")

	paused = false
	if _failures == 0:
		print("All runtime time-controller tests passed.")
	quit(_failures)


func _check(condition: bool, label: String) -> void:
	if condition:
		print("PASS: ", label)
		return
	_failures += 1
	push_error("FAIL: %s" % label)
