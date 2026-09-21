using System.Text.Json;

namespace GodotMcp.Daemon.Instances;

/// <summary>
/// 注册表 projects.json 中 by_path 的一行(编辑器实例条目)。形状真源:
/// addons/godot_mcp_toolkit/registry/store/registry_entry_file.gd build_entry +
/// registry_projection.gd(build_entry 去掉 _key 后即投影行)。
/// 注意:真实 addon 经 Godot JSON 写盘,所有数字是 float 形态(如 6550.0)——
/// 整数值按"整值 float→int"宽容解析(与线上 id 强转同一纪律)。
/// </summary>
/// <param name="Key">规范化项目路径(by_path 的键;解析方传入,JSON 行内无此字段)。</param>
/// <param name="Port">编辑器 WS 端口。</param>
/// <param name="TokenPath">令牌(token)文件绝对路径(ADR-0011 结构性校验对象)。</param>
/// <param name="Pid">编辑器进程 id(活性判定用)。</param>
/// <param name="GodotVersion">注册表自报引擎版本(空串 = 未知;版本读取中作 ack 之外的兜底路)。</param>
/// <param name="RuntimePort">运行时(runtime,游玩测试进程)WS 端口;未运行时为 null。</param>
/// <param name="RuntimePid">运行时进程 id;未运行时为 null。</param>
/// <param name="LspHost">语言服务器协议(LSP)主机;空串按 127.0.0.1 处理。</param>
/// <param name="LspPort">LSP 端口;null = 未配置(按受保护默认 6005 解析)。</param>
/// <param name="StartedAt">编辑器进程启动时刻(unix 秒);0 = 注册表未提供。与 pid 共同构成
/// 实例身份,用于死条目抑制判定(见 issue 20:仅凭 key 判身份会屏蔽合法的重启自愈)。</param>
public sealed record RegistryEntry(
    string Key, int Port, string TokenPath, int Pid, string GodotVersion,
    int? RuntimePort, int? RuntimePid, string LspHost, int? LspPort, double StartedAt = 0)
{
    /// <summary>
    /// 实例身份指纹(pid + 端口 + 令牌路径 + 启动时刻)。死条目抑制以它为准:
    /// 同一身份再次出现即认定是那条已消失的陈旧行,不再重建连接;
    /// 编辑器重启会改变 pid/started_at,指纹随之变化 —— 自愈路径不受抑制。
    /// </summary>
    public string Identity => $"{Pid}|{Port}|{TokenPath}|{StartedAt}";

    /// <summary>解析一行;缺关键字段(port/pid)的行按不可用跳过。</summary>
    /// <param name="key">by_path 对象的属性名(规范化项目键)。</param>
    /// <param name="row">该行的 JSON 值。</param>
    /// <returns>解析出的条目;行非对象或缺 port/pid 为 null(调用方跳过该行)。</returns>
    public static RegistryEntry? TryParse(string key, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var port = row.TryGetProperty("port", out var portEl) ? GetInt32(portEl) : null;
        var pid = row.TryGetProperty("pid", out var pidEl) ? GetInt32(pidEl) : null;
        if (port is null || pid is null)
        {
            return null;
        }

        var tokenPath = row.TryGetProperty("token_path", out var tp) && tp.ValueKind == JsonValueKind.String
            ? tp.GetString() ?? ""
            : "";
        var godotVersion = row.TryGetProperty("godot_version", out var gv) && gv.ValueKind == JsonValueKind.String
            ? gv.GetString() ?? ""
            : "";
        var runtimePort = row.TryGetProperty("runtime_port", out var rp) && rp.ValueKind == JsonValueKind.Number
            ? GetInt32(rp)
            : null;
        var runtimePid = row.TryGetProperty("runtime_pid", out var rpid) && rpid.ValueKind == JsonValueKind.Number
            ? GetInt32(rpid)
            : null;
        var lspHost = row.TryGetProperty("lsp_host", out var lh) && lh.ValueKind == JsonValueKind.String
            ? lh.GetString() ?? ""
            : "";
        var lspPort = row.TryGetProperty("lsp_port", out var lp) && lp.ValueKind == JsonValueKind.Number
            ? GetInt32(lp)
            : null;
        // started_at 是浮点 unix 秒(注册表写盘形态);缺失或非数值按 0(identity 退化为 pid|port|token)。
        var startedAt = row.TryGetProperty("started_at", out var sa) && sa.ValueKind == JsonValueKind.Number
            ? sa.GetDouble()
            : 0d;
        return new RegistryEntry(
            key, port.Value, tokenPath, pid.Value, godotVersion, runtimePort, runtimePid, lspHost, lspPort, startedAt);
    }

    /// <summary>整值宽容解析:整数直取;整值 float(如 6550.0)取整(与线上 id 强转同一纪律);
    /// 越界或非整数 float 返回 null。</summary>
    /// <param name="el">JSON 数值节点。</param>
    /// <returns>整数值;不可解析为 null。</returns>
    private static int? GetInt32(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (el.TryGetInt32(out var value))
        {
            return value;
        }

        var d = el.GetDouble();
        return double.IsInteger(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;
    }
}
