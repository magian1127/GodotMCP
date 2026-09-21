namespace GodotMcp.Daemon.Instances;

/// <summary>版本门控(工具名 + [min, max] 边界;Node godotMin/MaxVersion 语义)。</summary>
/// <param name="ToolName">门控对应的工具名(错误文案据此标识工具)。</param>
/// <param name="Min">最低支持版本("major.minor" 形态);null = 无下界。</param>
/// <param name="Max">最高支持版本("major.minor" 形态);null = 无上界。</param>
public sealed record VersionGate(string ToolName, string? Min, string? Max);

/// <summary>
/// Godot 版本门控助手(Node shared/version.ts 同算式):[major, minor] 元组比较,
/// 边界可选且含端点。未连接/版本未知一律视为不可验证(保守拒绝,Node 同规)。
/// </summary>
/// <remarks>消费方:InstanceManager —— 可见性判定 AnyVersionCompatible 与调用期门控 EnforceVersionGate。</remarks>
internal static class GodotVersions
{
    /// <summary>把 "major.minor" / "major.minor.patch" 解析为 (major, minor);无法解析返回 null。</summary>
    /// <param name="version">版本字符串,可空。</param>
    /// <returns>(major, minor) 元组;空串/段数不足/前两段非数字为 null(patch 及其后忽略)。</returns>
    public static (int Major, int Minor)? Parse(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }
        var parts = version.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor))
        {
            return null;
        }
        return (major, minor);
    }

    /// <summary>已连接版本是否落在 [min, max] 边界内(每个边界可选且含端点)。</summary>
    /// <param name="connected">已连接实例(instance)的版本字符串,可空。</param>
    /// <param name="min">最低边界,可空(含端点)。</param>
    /// <param name="max">最高边界,可空(含端点)。</param>
    /// <returns>版本可解析且不低于 min、不高于 max 为 true;版本不可解析(未连接/未知)一律 false。</returns>
    public static bool IsCompatible(string? connected, string? min, string? max)
    {
        var parsed = Parse(connected);
        if (parsed is null)
        {
            return false;
        }
        var (major, minor) = parsed.Value;
        if (min is not null)
        {
            var bound = Parse(min);
            if (bound is null || Compare((major, minor), bound.Value) < 0)
            {
                return false;
            }
        }
        if (max is not null)
        {
            var bound = Parse(max);
            if (bound is null || Compare((major, minor), bound.Value) > 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>[major, minor] 元组比较:主版本不同先比主版本,否则比次版本(Node 同算式)。</summary>
    /// <param name="a">左侧版本元组。</param>
    /// <param name="b">右侧版本元组。</param>
    /// <returns>负数 = a 更旧,0 = 相等,正数 = a 更新。</returns>
    private static int Compare((int Major, int Minor) a, (int Major, int Minor) b) =>
        a.Major != b.Major ? a.Major - b.Major : a.Minor - b.Minor;

    /// <summary>错误文案用的主次版本("4.4.2" → "4.4";Node 报错同读法)。</summary>
    public static string MajorMinor(string version) =>
        Parse(version) is { } v ? $"{v.Major}.{v.Minor}" : version;

    /// <summary>Node versionSupportText 原文(用于调用期门控错误的 hint)。
    /// <para>逻辑链:min 与 max 均非空 → 区间文案(含端点);仅 min 非空 → "Requires … or newer";
    /// 其余 → "Supported up to …"(按 max 渲染,含端点)。</para></summary>
    /// <param name="min">最低边界,可空。</param>
    /// <param name="max">最高边界,可空。</param>
    /// <returns>英文支持范围文案。</returns>
    public static string SupportText(string? min, string? max)
    {
        if (min is not null && max is not null)
        {
            return $"Supported on Godot {min}–{max} (inclusive).";
        }
        if (min is not null)
        {
            return $"Requires Godot {min} or newer.";
        }
        return $"Supported up to Godot {max} (inclusive).";
    }
}
