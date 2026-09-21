using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GodotMcp.Daemon.Instances;

/// <summary>
/// 机器级注册表(projects.json 聚合视图)的只读消费方 —— 与 Node 桥 registry.ts 同责:
/// 只读聚合文件,绝不写、绝不删除任何注册表文件(其 GC 归 addon)。
/// 令牌路径按 ADR-0011 语义结构性校验(token_path 是 addon 发布的全局化绝对路径;
/// 校验"格式+存在",绝不重算哈希)。工程身份哈希复刻 paths/project_key.gd
/// (sha256_text().substr(0,12),输入必须是已规范化键)。
/// </summary>
public static partial class RegistryReader
{
    /// <summary>注册表(registry)聚合文件名(位于 daemon 状态目录;addon 写、daemon 只读)。</summary>
    public const string ProjectsFileName = "projects.json";

    /// <summary>读取 projects.json 的 by_path 全集;文件缺失返回空表,解析失败抛异常(调用方按周期重试)。
    /// 以 FileShare.ReadWrite|Delete 打开:注册表写方的原子替换(REPLACE_EXISTING)需要目标可删,
    /// 默认共享模式会让写方瞬时 Access Denied(与 addon 写侧的重试纪律互为配合)。</summary>
    /// <param name="stateDir">daemon 状态目录。</param>
    /// <returns>可用条目列表(缺关键字段的行已跳过);文件缺失/结构不对为空表。</returns>
    public static IReadOnlyList<RegistryEntry> Read(string stateDir)
    {
        var path = Path.Combine(stateDir, ProjectsFileName);
        if (!File.Exists(path))
        {
            return Array.Empty<RegistryEntry>();
        }

        string text;
        using (var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("by_path", out var byPath) || byPath.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<RegistryEntry>();
        }

        var entries = new List<RegistryEntry>();
        foreach (var row in byPath.EnumerateObject())
        {
            var entry = RegistryEntry.TryParse(row.Name, row.Value);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    /// <summary>进程存活判定(Node 桥 isPidAlive 同义:Process.kill(pid,0) 的可移植等价)。</summary>
    /// <param name="pid">进程 id。</param>
    /// <returns>存活为 true;非正数/已退出/查询失败为 false。</returns>
    public static bool IsPidAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取并结构性校验 token_path 指向的令牌文件(ADR-0011):
    /// 绝对路径、无 ".." 段、文件存在、以 project_instance_&lt;12 位 hex&gt;/mcp_token 结尾。
    /// 每次连接前调用 —— 插件重启后的令牌轮换因而自愈。
    /// </summary>
    /// <param name="entry">注册表条目(取 token_path)。</param>
    /// <returns>令牌内容(trim 后非空);任一校验不过为 null。</returns>
    public static string? TryReadToken(RegistryEntry entry)
    {
        var tokenPath = entry.TokenPath;
        if (string.IsNullOrWhiteSpace(tokenPath) || !Path.IsPathRooted(tokenPath))
        {
            return null;
        }

        var normalized = tokenPath.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
            {
                return null;
            }
        }

        if (!PublishedTokenPathPattern().IsMatch(normalized))
        {
            return null;
        }

        if (!File.Exists(tokenPath))
        {
            return null;
        }

        var token = File.ReadAllText(tokenPath).Trim();
        return token.Length > 0 ? token : null;
    }

    /// <summary>已规范化键 → 12 位小写 hex(project_key.gd hash_of,输入按原样哈希)。</summary>
    /// <param name="canonicalKey">已规范化的项目键(不做二次规范化)。</param>
    /// <returns>12 位小写十六进制短 id(实例寻址别名)。</returns>
    public static string HashOf(string canonicalKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey))).ToLowerInvariant();
        return hex[..12];
    }

    /// <summary>
    /// 规范化项目根路径(project_key.gd canonical,冻结契约):反斜杠→正斜杠、去尾斜杠、
    /// win/mac 转小写。instance 参数的主标识按此解析后与注册表键比对。
    /// </summary>
    /// <param name="path">任意形态的项目路径。</param>
    /// <returns>规范化键(与注册表 by_path 同键形态)。</returns>
    public static string Canonical(string path)
    {
        var key = path.Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            key = key.ToLowerInvariant();
        }
        return key;
    }

    /// <summary>已发布令牌路径的形态:以 /project_instance_&lt;12 位小写 hex&gt;/mcp_token 结尾。</summary>
    [GeneratedRegex("/project_instance_[0-9a-f]{12}/mcp_token$")]
    private static partial Regex PublishedTokenPathPattern();
}
