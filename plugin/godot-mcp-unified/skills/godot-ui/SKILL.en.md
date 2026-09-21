---
name: godot-ui
description: Expert knowledge of Godot's UI system — Control nodes, theme customization, styling, responsive layouts, and common UI patterns for menus, HUDs, inventories, and dialogue systems — mapped onto the Godot MCP Unified tool surface. Use when creating or styling UI; hand reported failures to godot-debug and installation questions to godot-project-setup.
---

Language: English | [中文](SKILL.md)

# Godot UI

You are a Godot UI/UX expert, fluent in Godot's Control node system, Theme customization, responsive design, and common game UI patterns: menus, HUDs, inventories, and dialogue systems.

# Core UI knowledge

## Control node hierarchy

**Base Control properties:**
- `anchor_*`: anchor positioning relative to the parent's edges (0.0 to 1.0)
- `offset_*`: pixel offsets from the anchor positions
- `size_flags_*`: how the node stretches or shrinks
- `custom_minimum_size`: minimum size constraint
- `mouse_filter`: mouse input handling (STOP, PASS, IGNORE)
- `focus_mode`: keyboard/gamepad focus behavior

**Common Control nodes:**

### Container nodes (layout management)
- **VBoxContainer**: vertical stacking with automatic spacing
- **HBoxContainer**: horizontal arrangement with automatic spacing
- **GridContainer**: grid layout divided by column count
- **MarginContainer**: adds margins around its child
- **CenterContainer**: centers a single child
- **PanelContainer**: container with a panel background
- **ScrollContainer**: scrollable area for overflowing content
- **TabContainer**: tabbed interface
- **SplitContainer**: draggable divider between two children

### Interactive controls
- **Button**: standard clickable button
- **TextureButton**: button with textures for each state
- **CheckBox**: checkbox
- **CheckButton**: toggle-style switch control
- **OptionButton**: dropdown selection menu
- **LineEdit**: single-line text input
- **TextEdit**: multi-line text editor
- **Slider/HSlider/VSlider**: value adjustment sliders
- **SpinBox**: numeric input with increment/decrement buttons
- **ProgressBar**: progress bar
- **ItemList**: scrollable list of items
- **Tree**: hierarchical tree view

### Display nodes
- **Label**: text display
- **RichTextLabel**: rich text supporting BBCode, images, and effects
- **TextureRect**: image display with multiple stretch modes
- **NinePatchRect**: nine-patch sliced image
- **ColorRect**: solid color rectangle
- **VideoStreamPlayer**: video playback inside UI
- **GraphEdit/GraphNode**: node-graph interfaces

### Advanced controls
- **Popup**: modal/non-modal popup window
- **PopupMenu**: context menu
- **MenuBar**: top menu bar
- **FileDialog**: file picker
- **ColorPicker**: color picker
- **SubViewport**: embedded viewport, for 3D inside 2D UI

## Anchors and the container system

**Anchor presets:**
```gdscript
# Common anchor configurations
# Top-left (default): anchor_left=0, anchor_top=0, anchor_right=0, anchor_bottom=0
# Full rect: anchor_left=0, anchor_top=0, anchor_right=1, anchor_bottom=1
# Top wide: anchor_left=0, anchor_top=0, anchor_right=1, anchor_bottom=0
# Center: anchor_left=0.5, anchor_top=0.5, anchor_right=0.5, anchor_bottom=0.5
```

Anchors only drive layout for Controls whose parent is **not** a Container. A Container parent overrides its children's layout — under Containers, drive size through `size_flags_*` and `custom_minimum_size` instead.

**Responsive design pattern:**
```gdscript
# Responsive UI in _ready()
func _ready():
    # Connect the viewport size-changed signal
    get_viewport().size_changed.connect(_on_viewport_size_changed)
    _on_viewport_size_changed()

func _on_viewport_size_changed():
    var viewport_size = get_viewport_rect().size
    # Adjust UI based on aspect ratio or screen size
    if viewport_size.x / viewport_size.y < 1.5:  # portrait or square
        # Switch to the mobile layout
        pass
    else:  # landscape
        # Use the desktop layout
        pass
```

## The Theme system

**Theme structure:**
- **StyleBox**: control background styles (StyleBoxFlat, StyleBoxTexture)
- **Font**: font resources, with sizes and variations
- **Color**: named color values
- **Icon**: Texture2D used as icons and graphics
- **Constants**: numbers (separation, margins)

**Creating a Theme in code:**
```gdscript
# Create the theme
var theme = Theme.new()

# Styleboxes for the button
var style_normal = StyleBoxFlat.new()
style_normal.bg_color = Color(0.2, 0.2, 0.2)
style_normal.corner_radius_top_left = 5
style_normal.corner_radius_top_right = 5
style_normal.corner_radius_bottom_left = 5
style_normal.corner_radius_bottom_right = 5
style_normal.content_margin_left = 10
style_normal.content_margin_right = 10
style_normal.content_margin_top = 5
style_normal.content_margin_bottom = 5

var style_hover = StyleBoxFlat.new()
style_hover.bg_color = Color(0.3, 0.3, 0.3)
# ...same corner radius and margins

var style_pressed = StyleBoxFlat.new()
style_pressed.bg_color = Color(0.15, 0.15, 0.15)
# ...same corner radius and margins

theme.set_stylebox("normal", "Button", style_normal)
theme.set_stylebox("hover", "Button", style_hover)
theme.set_stylebox("pressed", "Button", style_pressed)

# Apply to the Control node
$MyControl.theme = theme
```

**Theme resources:**
Best practice: create `.tres` theme files and keep them under `resources/themes/`
- Editable visually in the inspector
- Shareable across scenes
- Support inheritance (base theme + overrides)

# Common UI patterns

### Main menu
```text
CanvasLayer
└── MarginContainer (screen edge padding)
    └── VBoxContainer (vertical menu layout)
        ├── TextureRect (logo)
        ├── VBoxContainer (button stack)
        │   ├── Button (new game)
        │   ├── Button (continue)
        │   ├── Button (settings)
        │   └── Button (quit)
        └── Label (version string)
```

### Settings menu
```text
CanvasLayer
├── ColorRect (dim overlay)
└── PanelContainer (settings panel)
    └── MarginContainer
        └── VBoxContainer
            ├── Label (settings title)
            ├── TabContainer
            │   ├── VBoxContainer (video tab)
            │   │   ├── HBoxContainer
            │   │   │   ├── Label (resolution:)
            │   │   │   └── OptionButton
            │   │   └── HBoxContainer
            │   │       ├── Label (fullscreen:)
            │   │       └── CheckBox
            │   └── VBoxContainer (audio tab)
            │       ├── HBoxContainer
            │       │   ├── Label (master volume:)
            │       │   └── HSlider
            │       └── HBoxContainer
            │           ├── Label (music volume:)
            │           └── HSlider
            └── HBoxContainer (button row)
                ├── Button (apply)
                └── Button (back)
```

### HUD
```text
CanvasLayer (layer = 10, renders on top)
└── MarginContainer (screen margins)
    └── VBoxContainer
        ├── HBoxContainer (top bar)
        │   ├── TextureRect (health icon)
        │   ├── ProgressBar (health bar)
        │   ├── Control (spacer)
        │   ├── Label (score)
        │   └── TextureRect (coin icon)
        ├── Control (flex spacer)
        └── HBoxContainer (bottom bar)
            ├── TextureButton (inventory)
            ├── TextureButton (map)
            └── TextureButton (pause)
```

### Inventory system
```text
CanvasLayer
├── ColorRect (overlay background)
└── PanelContainer (inventory panel)
    └── MarginContainer
        └── VBoxContainer
            ├── Label (inventory title)
            ├── HBoxContainer (main area)
            │   ├── GridContainer (item grid, columns=5)
            │   │   ├── TextureButton (item slot)
            │   │   ├── TextureButton (item slot)
            │   │   └── ... (more slots)
            │   └── PanelContainer (item details)
            │       └── VBoxContainer
            │           ├── TextureRect (item image)
            │           ├── Label (item name)
            │           ├── RichTextLabel (description)
            │           └── Button (use/equip)
            └── Button (close)
```

### Dialogue system
```text
CanvasLayer (layer = 5)
├── Control (spacer)
└── PanelContainer (dialogue box, anchored bottom)
    └── MarginContainer
        └── VBoxContainer
            ├── HBoxContainer (speaker info)
            │   ├── TextureRect (portrait)
            │   └── Label (speaker name)
            ├── RichTextLabel (dialogue text with BBCode)
            └── VBoxContainer (choice container)
                ├── Button (choice 1)
                ├── Button (choice 2)
                └── Button (choice 3)
```

### Pause menu
```text
CanvasLayer (layer = 100)
├── ColorRect (dim overlay, modulate alpha)
└── CenterContainer (full-rect anchors)
    └── PanelContainer (menu panel)
        └── MarginContainer
            └── VBoxContainer
                ├── Label (paused)
                ├── Button (resume)
                ├── Button (settings)
                ├── Button (main menu)
                └── Button (quit)
```

# Common UI script patterns

### Button signal connections
```gdscript
@onready var start_button = $VBoxContainer/StartButton

func _ready():
    # Connect the button signal
    start_button.pressed.connect(_on_start_button_pressed)

    # Or connect visually in the inspector

func _on_start_button_pressed():
    # Handle the button press
    get_tree().change_scene_to_file("res://scenes/main_game.tscn")
```

### Keyboard/gamepad menu navigation
```gdscript
func _ready():
    # Focus the first focusable button
    $VBoxContainer/StartButton.grab_focus()

    # Configure focus neighbors for gamepad navigation
    $VBoxContainer/StartButton.focus_neighbor_bottom = $VBoxContainer/SettingsButton.get_path()
    $VBoxContainer/SettingsButton.focus_neighbor_top = $VBoxContainer/StartButton.get_path()
    $VBoxContainer/SettingsButton.focus_neighbor_bottom = $VBoxContainer/QuitButton.get_path()
```

### Animated transitions
```gdscript
# Fade a menu in
func show_menu():
    modulate.a = 0
    visible = true
    var tween = create_tween()
    tween.tween_property(self, "modulate:a", 1.0, 0.3)

# Fade a menu out
func hide_menu():
    var tween = create_tween()
    tween.tween_property(self, "modulate:a", 0.0, 0.3)
    tween.tween_callback(func(): visible = false)

# Slide in from the side
func slide_in():
    position.x = -get_viewport_rect().size.x
    visible = true
    var tween = create_tween()
    tween.set_trans(Tween.TRANS_QUAD)
    tween.set_ease(Tween.EASE_OUT)
    tween.tween_property(self, "position:x", 0, 0.5)
```

### Dynamic lists
```gdscript
# Populate an ItemList dynamically
@onready var item_list = $ItemList

func populate_list(items: Array):
    item_list.clear()
    for item in items:
        item_list.add_item(item.name, item.icon)
        item_list.set_item_metadata(item_list.item_count - 1, item)

func _on_item_list_item_selected(index: int):
    var item = item_list.get_item_metadata(index)
    # Act on the selected entry
```

### Health bar updates
```gdscript
@onready var health_bar = $HealthBar
var current_health = 100
var max_health = 100

func _ready():
    health_bar.max_value = max_health
    health_bar.value = current_health

func take_damage(amount: int):
    current_health = max(0, current_health - amount)

    # Tween smoothly to the new value
    var tween = create_tween()
    tween.tween_property(health_bar, "value", current_health, 0.2)

    # Recolor by health percentage
    if current_health < max_health * 0.3:
        health_bar.modulate = Color.RED
    elif current_health < max_health * 0.6:
        health_bar.modulate = Color.YELLOW
    else:
        health_bar.modulate = Color.GREEN
```

### Modal popups
```gdscript
@onready var popup = $Popup

func show_confirmation(message: String, on_confirm: Callable):
    $Popup/VBoxContainer/Label.text = message
    popup.popup_centered()

    # Store the callback
    if not $Popup/VBoxContainer/HBoxContainer/ConfirmButton.pressed.is_connected(_on_confirm):
        $Popup/VBoxContainer/HBoxContainer/ConfirmButton.pressed.connect(_on_confirm)

    confirm_callback = on_confirm

var confirm_callback: Callable

func _on_confirm():
    popup.hide()
    if confirm_callback:
        confirm_callback.call()
```

# UI performance

**Best practices:**
1. Manage depth with **CanvasLayer**, not z_index
2. Set `clip_contents = true` on ScrollContainers to clip content
3. **Control RichTextLabel complexity** — BBCode parsing can be slow
4. **Pool UI elements** — reuse nodes instead of creating and destroying them
5. Use a **TextureAtlas** for UI textures to cut draw calls
6. Keep similar elements **under the same parent** for batching
7. Disable processing while hidden: `process_mode = PROCESS_MODE_DISABLED`
8. Use **Control.clip_contents** to avoid rendering off-screen elements

**Memory management:**
```gdscript
# Free UI scenes that are no longer used
func close_menu():
    queue_free()  # instead of merely hiding

# Pool high-churn UI elements
var button_pool = []
const MAX_POOL_SIZE = 20

func get_pooled_button():
    if button_pool.is_empty():
        return Button.new()
    return button_pool.pop_back()

func return_to_pool(button: Button):
    if button_pool.size() < MAX_POOL_SIZE:
        button.get_parent().remove_child(button)
        button_pool.append(button)
    else:
        button.queue_free()
```

# Accessibility features

**Text scaling:**
```gdscript
# Support a font-size preference
func apply_text_scale(scale: float):
    for label in get_tree().get_nodes_in_group("scalable_text"):
        if label is Label or label is RichTextLabel:
            label.add_theme_font_size_override("font_size", int(16 * scale))
```

**Gamepad support:**
```gdscript
# Make sure every interactive UI element works with a gamepad
func _ready():
    # Build the focus chain
    for i in range($ButtonContainer.get_child_count() - 1):
        var current = $ButtonContainer.get_child(i)
        var next = $ButtonContainer.get_child(i + 1)
        current.focus_neighbor_bottom = next.get_path()
        next.focus_neighbor_top = current.get_path()

    # Focus the first button
    if $ButtonContainer.get_child_count() > 0:
        $ButtonContainer.get_child(0).grab_focus()
```

# Building UI through the MCP toolkit

All tools come from the **godot-mcp-unified** MCP server — call them with whatever prefix your client displays. Tools marked *(group)* live in on-demand groups: load them first with `discover_tools({request: "..."})`, keep at most three groups active at a time, and release them with `discover_tools({reset: [...]})` once the phase is done. For general toolkit workflow — batching, error recovery, token discipline — follow the godot-control skill.

When creating UI elements:

1. **`scene_create`** creates the `.tscn` on disk (`root_type` can be `CanvasLayer`, `Control`, …; `if_exists:"return"` keeps it idempotent). It does **not** open the scene in the editor.
2. **`scene_open`** — open the new scene right after creating it. Node tools always operate on the currently open scene: without this step they either fail with `NO_SCENE` or modify the wrong scene.
3. **`scene_create_node`** builds the Control hierarchy. Idempotent (a name clash returns `returned`). When the parent is a Container the child Control automatically gets `layout_mode=1`. Pass initial properties inline (`properties={text:"New Game", columns:5}`) and mark script-referenced nodes `unique_name:true` for `%Name` access — both save round-trips.
4. **`control_set_layout`** *(node_advanced group)* applies an anchor preset in one call (`PRESET_FULL_RECT`, `PRESET_BOTTOM_WIDE`, `PRESET_CENTER`, …) plus optional pixel `margins`; it wraps `set_anchors_and_offsets_preset()` and returns `final_rect` for verification. Remember the container rule above: anchors matter only for Controls outside Containers.
5. **`node_set_property`** sets single or `batch` properties (`size_flags_*`, `custom_minimum_size`, `mouse_filter`, `focus_mode`, `focus_neighbor_*`, …). It supports `/`-composite paths and `theme_override_*` properties; assign a `.tres` resource with the Resource wrapper `{type:"Resource", path:"res://themes/ui_theme.tres"}`.
6. **`editor_save_scene`** saves once the structure is in place.
7. Write GDScript with the host's file tools, attach it with **`node_set_script`**, and validate with **`script_check`** (offline — works without the editor running).
8. **`asset_import`** *(asset_ops group)* imports UI textures, icons, and fonts: exactly one of `source_path` (absolute or `res://`) or `base64_data`, plus the required `dest_path`.
9. **`theme_edit`** *(theme group)* creates or modifies `.tres` themes with a batch `edits` array (`{type_name, property_type, property, value}` for colors, constants, fonts, font sizes, icons, and styleboxes). It is write-only — there is no theme read-back tool, so track what you wrote and apply the finished resource to Controls yourself (step 5).
10. **`node_groups`** *(node_advanced group)* adds nodes to groups (e.g. the `scalable_text` group from the accessibility section).
11. **Verify at runtime**: `game_start(scene_path="res://…")` → `input_simulate` (`click_node` with `{node_path}` presses buttons without guessing coordinates; `send_text` types into the focused Control and fires the real `text_changed`/`text_submitted` signals; `action` fires mapped input actions) → `capture_screenshot` / `runtime_inspect_node`. To check layout without running the game, `capture_screenshot` with `target:"editor"` and a `node_path` focuses and frames that node in the editor viewport.

The `unsafe` group (`execute_code`, `node_call_method`) is absent unless the server was started with `GODOT_MCP_UNSAFE=1`. Never rely on calling editor-side methods to set up UI state — express state through the typed tools above and verify through playtest.

## Example workflow

```text
1. scene_create(file_path="res://scenes/ui/main_menu.tscn", root_type="CanvasLayer")
2. scene_open(file_path="res://scenes/ui/main_menu.tscn")
3. scene_create_node(class_name="MarginContainer", parent_path=".", node_name="Margins")
4. scene_create_node(class_name="VBoxContainer", parent_path="./Margins", node_name="Menu")
5. scene_create_node(class_name="Button", parent_path="./Menu", node_name="NewGameButton",
                     properties={text: "New Game"}, unique_name=true)
6. discover_tools({request: "node_advanced"})
   → control_set_layout(node_path="./Margins", preset="PRESET_FULL_RECT")
7. editor_save_scene()
8. write main_menu.gd → node_set_script → script_check
9. game_start(scene_path="res://scenes/ui/main_menu.tscn")
   → input_simulate → capture_screenshot
```

# Important reminders

- Always consider **keyboard/gamepad navigation**, not just mouse
- Manage render order with **CanvasLayer** to avoid z-fighting
- **Anchor presets** are the responsive-design workhorse — outside Containers
- **Themes** should be resources, reused across scenes
- **Signal connections** are the primary way to handle UI interaction
- **Tweens** make UI transitions feel smooth and polished
- **Test at multiple resolutions** — via Project Settings > Display > Window
