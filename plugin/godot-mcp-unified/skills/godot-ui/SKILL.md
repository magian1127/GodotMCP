---
name: godot-ui
description: Godot UI 系统专家知识——Control 节点、主题(Theme)定制、样式、响应式布局，以及菜单、HUD、背包、对话系统等常见游戏 UI 模式，并映射到 Godot MCP Unified 的工具面。创建或样式化 UI、搭建菜单/HUD/背包/对话界面时使用；已报告的故障转交 godot-debug，安装问题转交 godot-project-setup。
---

语言：中文 | [英文版](SKILL.en.md)

# Godot UI

你是一位 Godot UI/UX 专家，深谙 Godot 的 Control 节点系统、主题(Theme)定制、响应式设计，以及菜单、HUD、背包、对话系统等常见游戏 UI 模式。

# UI 核心知识

## Control 节点层级

**Control 基础节点属性：**
- `anchor_*`：相对于父节点边缘的锚点定位（0.0 到 1.0）
- `offset_*`：距锚点位置的像素偏移
- `size_flags_*`：节点的伸展/收缩方式
- `custom_minimum_size`：最小尺寸约束
- `mouse_filter`：鼠标输入处理方式（STOP、PASS、IGNORE）
- `focus_mode`：键盘/手柄焦点行为

**常用 Control 节点：**

### 容器节点（布局管理）
- **VBoxContainer**：垂直堆叠，自动排列间距
- **HBoxContainer**：水平排列，自动排列间距
- **GridContainer**：按列数划分的网格布局
- **MarginContainer**：在子节点四周添加边距
- **CenterContainer**：居中单个子节点
- **PanelContainer**：带面板背景的容器
- **ScrollContainer**：内容溢出时可滚动的区域
- **TabContainer**：多页签界面
- **SplitContainer**：两个子节点间可拖拽调节的分隔区

### 交互控件
- **Button**：标准可点击按钮
- **TextureButton**：用贴图表现各状态的按钮
- **CheckBox**：复选框
- **CheckButton**：开关样式的切换控件
- **OptionButton**：下拉选择菜单
- **LineEdit**：单行文本输入
- **TextEdit**：多行文本编辑器
- **Slider/HSlider/VSlider**：数值调节滑条
- **SpinBox**：带增减按钮的数值输入
- **ProgressBar**：进度条
- **ItemList**：可滚动的条目列表
- **Tree**：层级树视图

### 显示节点
- **Label**：文本显示
- **RichTextLabel**：支持 BBCode 格式、图片与特效的富文本
- **TextureRect**：图片显示，支持多种缩放方式
- **NinePatchRect**：九宫格切片缩放图片
- **ColorRect**：纯色矩形
- **VideoStreamPlayer**：UI 内视频播放
- **GraphEdit/GraphNode**：节点图界面

### 高级控件
- **Popup**：模态/非模态弹出窗口
- **PopupMenu**：上下文菜单
- **MenuBar**：顶部菜单栏
- **FileDialog**：文件选择器
- **ColorPicker**：颜色选择器
- **SubViewport**：内嵌视口，用于在 2D UI 中呈现 3D

## 锚点与容器系统

**锚点预设：**
```gdscript
# Common anchor configurations
# Top-left (default): anchor_left=0, anchor_top=0, anchor_right=0, anchor_bottom=0
# Full rect: anchor_left=0, anchor_top=0, anchor_right=1, anchor_bottom=1
# Top wide: anchor_left=0, anchor_top=0, anchor_right=1, anchor_bottom=0
# Center: anchor_left=0.5, anchor_top=0.5, anchor_right=0.5, anchor_bottom=0.5
```

锚点只对父节点**不是**容器的 Control 生效。容器父节点会覆盖子节点的布局——在容器之下，应改用 `size_flags_*` 和 `custom_minimum_size` 控制尺寸。

**响应式设计模式：**
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

## 主题系统(Theme)

**Theme 结构：**
- **样式框(StyleBox)**：控件背景样式（StyleBoxFlat、StyleBoxTexture）
- **字体(Font)**：字体资源，含字号与变体
- **颜色(Color)**：具名颜色值
- **图标(Icon)**：用作图标与图形的 Texture2D
- **常量(Constants)**：数值（间距、边距）

**在代码中创建 Theme：**
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

**Theme 资源：**
最佳实践：创建 `.tres` 主题文件并保存在 `resources/themes/` 目录
- 可在检查器(Inspector)中可视化编辑
- 可在多个场景间共享
- 支持继承（基础主题 + 覆盖）

# 常见 UI 模式

### 主菜单
```text
CanvasLayer
└── MarginContainer（屏幕边缘留白）
    └── VBoxContainer（垂直菜单布局）
        ├── TextureRect（logo）
        ├── VBoxContainer（按钮容器）
        │   ├── Button（新游戏）
        │   ├── Button（继续）
        │   ├── Button（设置）
        │   └── Button（退出）
        └── Label（版本信息）
```

### 设置菜单
```text
CanvasLayer
├── ColorRect（半透明遮罩）
└── PanelContainer（设置面板）
    └── MarginContainer
        └── VBoxContainer
            ├── Label（设置标题）
            ├── TabContainer
            │   ├── VBoxContainer（图像页签）
            │   │   ├── HBoxContainer
            │   │   │   ├── Label（分辨率：）
            │   │   │   └── OptionButton
            │   │   └── HBoxContainer
            │   │       ├── Label（全屏：）
            │   │       └── CheckBox
            │   └── VBoxContainer（音频页签）
            │       ├── HBoxContainer
            │       │   ├── Label（主音量：）
            │       │   └── HSlider
            │       └── HBoxContainer
            │           ├── Label（音乐音量：）
            │           └── HSlider
            └── HBoxContainer（按钮行）
                ├── Button（应用）
                └── Button（返回）
```

### HUD（抬头显示）
```text
CanvasLayer（layer = 10，置顶渲染）
└── MarginContainer（屏幕边距）
    └── VBoxContainer
        ├── HBoxContainer（顶栏）
        │   ├── TextureRect（血量图标）
        │   ├── ProgressBar（血条）
        │   ├── Control（占位）
        │   ├── Label（分数）
        │   └── TextureRect（金币图标）
        ├── Control（弹性占位）
        └── HBoxContainer（底栏）
            ├── TextureButton（背包）
            ├── TextureButton（地图）
            └── TextureButton（暂停）
```

### 背包系统
```text
CanvasLayer
├── ColorRect（遮罩背景）
└── PanelContainer（背包面板）
    └── MarginContainer
        └── VBoxContainer
            ├── Label（背包标题）
            ├── HBoxContainer（主区域）
            │   ├── GridContainer（物品网格，columns=5）
            │   │   ├── TextureButton（物品格）
            │   │   ├── TextureButton（物品格）
            │   │   └── ...（更多格子）
            │   └── PanelContainer（物品详情）
            │       └── VBoxContainer
            │           ├── TextureRect（物品图片）
            │           ├── Label（物品名称）
            │           ├── RichTextLabel（描述）
            │           └── Button（使用/装备）
            └── Button（关闭）
```

### 对话系统
```text
CanvasLayer（layer = 5）
├── Control（占位）
└── PanelContainer（对话框，锚定底部）
    └── MarginContainer
        └── VBoxContainer
            ├── HBoxContainer（角色信息）
            │   ├── TextureRect（角色立绘）
            │   └── Label（角色名）
            ├── RichTextLabel（带 BBCode 的对话文本）
            └── VBoxContainer（选项容器）
                ├── Button（选项 1）
                ├── Button（选项 2）
                └── Button（选项 3）
```

### 暂停菜单
```text
CanvasLayer（layer = 100）
├── ColorRect（半透明遮罩，modulate alpha）
└── CenterContainer（全屏锚点）
    └── PanelContainer（菜单面板）
        └── MarginContainer
            └── VBoxContainer
                ├── Label（已暂停）
                ├── Button（继续）
                ├── Button（设置）
                ├── Button（主菜单）
                └── Button（退出）
```

# 常见 UI 脚本模式

### 按钮信号连接
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

### 键盘/手柄菜单导航
```gdscript
func _ready():
    # Focus the first focusable button
    $VBoxContainer/StartButton.grab_focus()

    # Configure focus neighbors for gamepad navigation
    $VBoxContainer/StartButton.focus_neighbor_bottom = $VBoxContainer/SettingsButton.get_path()
    $VBoxContainer/SettingsButton.focus_neighbor_top = $VBoxContainer/StartButton.get_path()
    $VBoxContainer/SettingsButton.focus_neighbor_bottom = $VBoxContainer/QuitButton.get_path()
```

### 动画过渡
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

### 动态列表
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

### 血条更新
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

### 模态弹窗
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

# UI 性能优化

**最佳实践：**
1. 尽量用 **CanvasLayer** 管理深度层级，而不是 z_index
2. 在 ScrollContainer 中设置 `clip_contents = true` 裁剪内容
3. **控制 RichTextLabel 复杂度** —— BBCode 解析可能较慢
4. **池化 UI 元素** —— 复用节点，而非反复创建/销毁
5. UI 贴图使用 **TextureAtlas**，减少绘制调用(draw call)
6. 相似元素**集中放在同一父节点**下批量处理
7. UI 隐藏时禁用处理：`process_mode = PROCESS_MODE_DISABLED`
8. 用 **Control.clip_contents** 避免渲染屏幕外元素

**内存管理：**
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

# 无障碍特性

**文字缩放：**
```gdscript
# Support a font-size preference
func apply_text_scale(scale: float):
    for label in get_tree().get_nodes_in_group("scalable_text"):
        if label is Label or label is RichTextLabel:
            label.add_theme_font_size_override("font_size", int(16 * scale))
```

**手柄支持：**
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

# 通过 MCP 工具包搭建 UI

所有工具来自 **godot-mcp-unified** MCP 服务器——以当前客户端实际显示的前缀调用。标注 *(组)* 的工具位于按需工具组：先通过 `discover_tools({request: "..."})` 激活，普通工作同时保持不超过三个组处于激活状态，阶段结束后用 `discover_tools({reset: [...]})` 释放。通用的工具包工作流（批量调用、错误恢复、token 纪律）遵循 godot-control 技能。

创建 UI 元素时，你应该：

1. **`scene_create`** 在磁盘上创建 `.tscn`（`root_type` 可为 `CanvasLayer`、`Control` 等；`if_exists:"return"` 保证幂等）。它**不会**在编辑器中打开该场景。
2. **`scene_open`** —— 创建后立即打开新场景。节点工具始终作用于当前打开的场景：缺少这一步要么报 `NO_SCENE`，要么改错场景。
3. **`scene_create_node`** 搭建 Control 节点层级。幂等（重名返回 `returned` 而非报错）。父节点为 Container 时，子 Control 自动获得 `layout_mode=1`。创建时可直接内联初始属性（`properties={text:"New Game", columns:5}`），并为脚本要引用的节点标记 `unique_name:true` 以便用 `%Name` 访问——两者都能省去往返调用。
4. **`control_set_layout`**（node_advanced 组）一次调用应用锚点预设（`PRESET_FULL_RECT`、`PRESET_BOTTOM_WIDE`、`PRESET_CENTER` 等），可附加像素级 `margins`；封装了 `set_anchors_and_offsets_preset()` 并返回 `final_rect` 供核对。记住上面的容器规则：锚点只对容器之外的 Control 有意义。
5. **`node_set_property`** 支持单项或 `batch` 批量设置属性（`size_flags_*`、`custom_minimum_size`、`mouse_filter`、`focus_mode`、`focus_neighbor_*` 等）。支持含 `/` 的复合路径和 `theme_override_*` 属性；挂载 `.tres` 资源时使用 Resource 包装 `{type:"Resource", path:"res://themes/ui_theme.tres"}`。
6. **`editor_save_scene`** 在结构搭建完成后保存。
7. 用宿主的文件工具编写 GDScript，用 **`node_set_script`** 附加脚本，再用 **`script_check`** 校验（离线——无需编辑器运行）。
8. **`asset_import`**（asset_ops 组）导入 UI 贴图、图标与字体：`source_path`（绝对路径或 `res://`）与 `base64_data` 必须且只能提供一个，外加必填的 `dest_path`。
9. **`theme_edit`**（theme 组）用批量 `edits` 数组创建或修改 `.tres` 主题（`{type_name, property_type, property, value}`，涵盖颜色、常量、字体、字号、图标与样式框）。它是只写的——没有主题读取回传工具，因此自己记录写入了什么，并把成品资源挂载到 Control 上（见第 5 步）。
10. **`node_groups`**（node_advanced 组）把节点加入组（例如上文无障碍一节中的 `scalable_text` 组）。
11. **运行时验证**：`game_start(scene_path="res://…")` → `input_simulate`（`click_node` 配 `{node_path}` 按下按钮，无需猜测坐标；`send_text` 向聚焦的 Control 输入文本并触发真实的 `text_changed`/`text_submitted` 信号；`action` 触发已映射的输入动作）→ `capture_screenshot` / `runtime_inspect_node`。不启动游戏检查布局时，可对 `capture_screenshot` 传 `target:"editor"` 加 `node_path`，编辑器视口会聚焦并框选该节点。

`unsafe` 组（`execute_code`、`node_call_method`）只有服务器以 `GODOT_MCP_UNSAFE=1` 启动时才存在。不要依赖调用编辑器侧方法来设置 UI 状态——用上面的类型化工具表达状态，并通过游玩测试验证。

## 示例工作流

```text
1. scene_create(file_path="res://scenes/ui/main_menu.tscn", root_type="CanvasLayer")   # 新建场景文件
2. scene_open(file_path="res://scenes/ui/main_menu.tscn")   # 打开为当前编辑场景
3. scene_create_node(class_name="MarginContainer", parent_path=".", node_name="Margins")   # 建边距容器
4. scene_create_node(class_name="VBoxContainer", parent_path="./Margins", node_name="Menu")   # 建垂直布局
5. scene_create_node(class_name="Button", parent_path="./Menu",   # 建按钮
                     node_name="NewGameButton", properties={text: "New Game"}, unique_name=true)   # 内联属性并标记唯一名
6. discover_tools({request: "node_advanced"})   # 按需加载工具组
   → control_set_layout(node_path="./Margins", preset="PRESET_FULL_RECT")   # 应用全屏锚点
7. editor_save_scene()   # 保存场景
8. 编写 main_menu.gd → node_set_script → script_check   # 挂脚本并离线校验
9. game_start(scene_path="res://scenes/ui/main_menu.tscn")   # 启动游玩测试
   → input_simulate → capture_screenshot   # 模拟输入并截图验证
```

# 重要提醒

- 除鼠标外，始终考虑**键盘/手柄导航**
- 用 **CanvasLayer** 管理渲染顺序，避免 z 轴争用(z-fighting)
- **锚点预设**是响应式设计的利器——仅在容器之外生效
- **Theme** 应做成资源以便复用
- **信号连接**是处理 UI 交互的首要方式
- **Tween** 补间动画让 UI 过渡更顺滑精致
- **在多种分辨率下测试** —— 使用 项目设置(Project Settings) > 显示(Display) > 窗口(Window)
