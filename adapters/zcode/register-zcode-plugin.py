# 把 godot-mcp-unified@godot-mcp-local 插件注册进 ZCode 的插件状态。
# 仓库根目录即本地插件市场(marketplace)
# （marketplace.json -> ./plugin/godot-mcp-unified）；
# 唯一真源(single source of truth)是 plugin/godot-mcp-unified。
# 幂等：可重复执行；已有条目会被保留或刷新。
import io
import json
import os
from datetime import datetime, timezone

HOME = os.path.expanduser("~")
ZCODE_PLUGINS = os.path.join(HOME, ".zcode", "cli", "plugins")
CONFIG_JSON = os.path.join(HOME, ".zcode", "cli", "config.json")

MARKETPLACE_ID = "godot-mcp-local"
PLUGIN_NAME = "godot-mcp-unified"
PLUGIN_ID = f"{PLUGIN_NAME}@{MARKETPLACE_ID}"
# 仓库根目录 = 市场根目录；插件源码位于 ./plugin/godot-mcp-unified。
# 直接按脚本自身位置推导（脚本位于 <仓库根>/adapters/zcode/），无需任何配置；
# ZCode 缓存是指向仓库的 junction，经缓存运行时推导值同样正确。
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MARKETPLACE_SOURCE = REPO_ROOT
PLUGIN_SOURCE_REL = "./plugin/godot-mcp-unified"


def load(path):
    with io.open(path, encoding="utf-8") as f:
        return json.load(f)


def save(path, data):
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")


def main():
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + (
        f"{datetime.now(timezone.utc).microsecond // 1000:03d}Z"
    )

    manifest_path = os.path.join(
        ZCODE_PLUGINS, "cache", MARKETPLACE_ID, PLUGIN_NAME, ".zcode-plugin", "plugin.json"
    )
    if not os.path.isfile(manifest_path):
        # 尚无缓存时，退回唯一的插件源码目录。
        manifest_path = os.path.join(
            MARKETPLACE_SOURCE, PLUGIN_SOURCE_REL, ".zcode-plugin", "plugin.json"
        )
    if not os.path.isfile(manifest_path):
        raise SystemExit(f"plugin manifest missing (looked in cache and {MARKETPLACE_SOURCE})")
    plugin_version = str(load(manifest_path).get("version") or "0.0.0")
    install_path = os.path.join(
        ZCODE_PLUGINS, "cache", MARKETPLACE_ID, PLUGIN_NAME, plugin_version
    )
    if not os.path.isfile(os.path.join(install_path, ".zcode-plugin", "plugin.json")):
        raise SystemExit(f"plugin manifest missing under {install_path}")

    # 1. installed_plugins.json（已安装插件清单）
    ip_path = os.path.join(ZCODE_PLUGINS, "installed_plugins.json")
    installed = load(ip_path)
    entry = {
        "id": PLUGIN_ID,
        "name": PLUGIN_NAME,
        "marketplace": MARKETPLACE_ID,
        "version": plugin_version,
        "installPath": install_path,
        "installedAt": now,
        "updatedAt": now,
        "scope": "user",
        "source": PLUGIN_SOURCE_REL,
    }
    before = len(installed["plugins"])
    installed["plugins"] = [p for p in installed["plugins"] if p.get("id") != PLUGIN_ID]
    installed["plugins"].append(entry)
    save(ip_path, installed)
    print(f"installed_plugins.json: {before} -> {len(installed['plugins'])} entries (added {PLUGIN_ID})")

    # 2. known_marketplaces.json（已知市场清单）
    km_path = os.path.join(ZCODE_PLUGINS, "known_marketplaces.json")
    known = load(km_path)
    mkt = {
        "id": MARKETPLACE_ID,
        "source": {"source": "directory", "path": MARKETPLACE_SOURCE},
        "name": MARKETPLACE_ID,
        "description": "Godot MCP Unified local plugin marketplace (single plugin source).",
        "addedAt": now,
        "lastUpdated": now,
        "pluginCount": 1,
    }
    known["marketplaces"] = [m for m in known["marketplaces"] if m.get("id") != MARKETPLACE_ID]
    known["marketplaces"].append(mkt)
    save(km_path, known)
    print(f"known_marketplaces.json: {len(known['marketplaces'])} marketplaces (ensured {MARKETPLACE_ID})")

    # 3. config.json 中的启用开关
    cfg = load(CONFIG_JSON)
    cfg.setdefault("plugins", {}).setdefault("enabledPlugins", {})[PLUGIN_ID] = True
    save(CONFIG_JSON, cfg)
    print(f"config.json: enabledPlugins[{PLUGIN_ID}] = True")

    # 4. 插件专属数据目录（与其他已安装插件的目录布局保持一致）
    data_dir = os.path.join(ZCODE_PLUGINS, "data", PLUGIN_ID)
    os.makedirs(data_dir, exist_ok=True)
    print(f"data dir ready: {data_dir}")


if __name__ == "__main__":
    main()
