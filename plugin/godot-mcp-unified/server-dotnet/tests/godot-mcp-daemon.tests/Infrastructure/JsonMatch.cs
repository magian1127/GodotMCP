using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// wire fixture 期望值的 C# 侧匹配器,语义与 Node harness(deepMatch)对齐:
/// 期望对象的键集合即实际键集合(严格深比较,钉住形状);叶子支持
/// { "$regex": … } 与 { "$untrusted": {kind, source, bodyJson} } 匹配器。
/// </summary>
internal static partial class JsonMatch
{
    /// <summary>untrusted 信封形状(security/untrusted.gd wrap):nonce 8 位 hex,首尾标签同 nonce。</summary>
    [GeneratedRegex("^<untrusted-([0-9a-f]{8}) kind=\"([^\"]+)\" source=\"([^\"]+)\">\n([\\s\\S]*)\n</untrusted-\\1>$")]
    private static partial Regex UntrustedEnvelope();

    /// <summary>
    /// 严格深比较入口:期望对象的键集合必须与实际完全一致,逐键递归;数组同长逐项比较;
    /// 叶子支持 $regex / $untrusted 匹配器。失败统一抛 InvalidOperationException,消息带 path 定位。
    /// 逻辑链:对象 → 识别匹配器($regex 只匹配字符串,$untrusted 解信封后递归)→ 键集合不等即抛
    /// → 逐键递归;数组 → 类型/长度不符即抛 → 逐项递归;数值 → 见 NumberEquals;字符串 → 逐字符相等;
    /// 其余字面量 → 原文文本相等。
    /// </summary>
    /// <param name="actual">实际值(响应体/通知等运行时观测)。</param>
    /// <param name="expected">期望值(fixture 期望子树,可含匹配器)。</param>
    /// <param name="path">当前比较路径标签(如 "[fx-01].authAck",仅用于失败消息定位)。</param>
    public static void DeepMatch(JsonElement actual, JsonElement expected, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if ("$regex" == GetMatcherKey(expected, out var regex))
                {
                    if (actual.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException($"{path}: 期望字符串以做 $regex 匹配,实际 {actual.ValueKind}");
                    }
                    if (!Regex.IsMatch(actual.GetString()!, regex!))
                    {
                        throw new InvalidOperationException($"{path}: 不匹配 /{regex}/,实际 {actual.GetString()}");
                    }
                    return;
                }
                if ("$untrusted" == GetMatcherKey(expected, out _))
                {
                    MatchUntrusted(actual, expected.GetProperty("$untrusted"), path);
                    return;
                }
                if (actual.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"{path}: 期望对象,实际 {actual.GetRawText()}");
                }
                var actualKeys = actual.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
                var expectedKeys = expected.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
                if (!actualKeys.SequenceEqual(expectedKeys))
                {
                    throw new InvalidOperationException(
                        $"{path}: 键集合不一致(严格深比较) 期望 [{string.Join(",", expectedKeys)}] 实际 [{string.Join(",", actualKeys)}]");
                }
                foreach (var p in expected.EnumerateObject())
                {
                    DeepMatch(actual.GetProperty(p.Name), p.Value, $"{path}.{p.Name}");
                }
                return;
            }
            case JsonValueKind.Array:
            {
                if (actual.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"{path}: 期望数组,实际 {actual.GetRawText()}");
                }
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                if (actualItems.Length != expectedItems.Length)
                {
                    throw new InvalidOperationException($"{path}: 数组长度不符({actualItems.Length} ≠ {expectedItems.Length})");
                }
                for (var i = 0; i < expectedItems.Length; i++)
                {
                    DeepMatch(actualItems[i], expectedItems[i], $"{path}[{i}]");
                }
                return;
            }
            case JsonValueKind.Number:
                if (actual.ValueKind != JsonValueKind.Number || !NumberEquals(actual, expected))
                {
                    throw new InvalidOperationException($"{path}: 期望 {expected.GetRawText()},实际 {actual.GetRawText()}");
                }
                return;
            case JsonValueKind.String when actual.ValueKind != JsonValueKind.String || actual.GetString() != expected.GetString():
                throw new InvalidOperationException($"{path}: 期望 {expected.GetRawText()},实际 {actual.GetRawText()}");
            case JsonValueKind.String:
                return;
            default:
                if (actual.GetRawText() != expected.GetRawText())
                {
                    throw new InvalidOperationException($"{path}: 期望 {expected.GetRawText()},实际 {actual.GetRawText()}");
                }
                return;
        }
    }

    /// <summary>
    /// 校验 untrusted 信封字符串并递归匹配正文。
    /// 逻辑链:实际须为字符串 → 正则解信封(nonce 8 位 hex,首尾标签同 nonce) →
    /// kind/source 与期望比对 → 带 bodyJson 时把正文按 JSON 解析后递归 DeepMatch;
    /// 不合形/kind 或 source 不符/正文非合法 JSON/正文不匹配即抛。
    /// </summary>
    /// <param name="actual">实际值(应为信封字符串)。</param>
    /// <param name="expected">$untrusted 匹配器对象(kind/source/bodyJson?)。</param>
    /// <param name="path">当前比较路径标签(失败消息定位)。</param>
    private static void MatchUntrusted(JsonElement actual, JsonElement expected, string path)
    {
        if (actual.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{path}: 期望 untrusted 信封字符串,实际 {actual.ValueKind}");
        }
        var m = UntrustedEnvelope().Match(actual.GetString()!);
        if (!m.Success)
        {
            throw new InvalidOperationException($"{path}: untrusted 信封不合形: {actual.GetString()![..Math.Min(120, actual.GetString()!.Length)]}");
        }
        var kind = expected.GetProperty("kind").GetString();
        var source = expected.GetProperty("source").GetString();
        if (m.Groups[2].Value != kind)
        {
            throw new InvalidOperationException($"{path}: kind 不符({m.Groups[2].Value} ≠ {kind})");
        }
        if (m.Groups[3].Value != source)
        {
            throw new InvalidOperationException($"{path}: source 不符({m.Groups[3].Value} ≠ {source})");
        }
        if (expected.TryGetProperty("bodyJson", out var bodyJson))
        {
            JsonElement body;
            try
            {
                body = JsonDocument.Parse(m.Groups[4].Value).RootElement;
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"{path}: 信封正文不是合法 JSON");
            }
            DeepMatch(body, bodyJson, $"{path}.body");
        }
    }

    /// <summary>识别期望对象是否为单键匹配器($regex/$untrusted;非对象、多键或普通键均不算)。</summary>
    /// <param name="expected">期望节点。</param>
    /// <param name="value">输出匹配器的参数($regex 的模式串;非字符串时 null)。</param>
    /// <returns>匹配器键名;不是匹配器时 null。</returns>
    private static string? GetMatcherKey(JsonElement expected, out string? value)
    {
        value = null;
        if (expected.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var props = expected.EnumerateObject().ToArray();
        if (props.Length != 1)
        {
            return null;
        }
        if (props[0].Name is "$regex" or "$untrusted")
        {
            value = props[0].Value.ValueKind == JsonValueKind.String ? props[0].Value.GetString() : null;
            return props[0].Name;
        }
        return null;
    }

    /// <summary>数值相等:两侧皆可解析为 Int64 时精确比较,否则按双精度 1e-9 容差(容忍 GDScript/C# 浮点差异)。</summary>
    /// <param name="a">实际数值节点。</param>
    /// <param name="b">期望数值节点。</param>
    /// <returns>数值语义相等则为 true。</returns>
    private static bool NumberEquals(JsonElement a, JsonElement b)
    {
        if (a.TryGetInt64(out var la) && b.TryGetInt64(out var lb))
        {
            return la == lb;
        }
        return Math.Abs(a.GetDouble() - b.GetDouble()) < 1e-9;
    }

    /// <summary>与 GDScript ProjectKey 相同的规范化(契约冻结):反斜杠→正斜杠、去尾斜杠、win/mac 转小写。</summary>
    /// <param name="path">任意形态的项目根路径。</param>
    /// <returns>规范化键(注册表 _key 与哈希输入)。</returns>
    public static string CanonicalProjectKey(string path)
    {
        var key = path.Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            key = key.ToLowerInvariant();
        }
        return key;
    }

    /// <summary>GDScript sha256_text().substr(0,12) 等价:小写 hex 前 12 位(UTF-8 输入)。</summary>
    /// <param name="canonicalKey">已规范化的项目键(CanonicalProjectKey 的输出)。</param>
    /// <returns>12 位小写十六进制哈希(条目/实例目录文件名)。</returns>
    public static string ProjectHashOf(string canonicalKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey))).ToLowerInvariant();
        return hex[..12];
    }
}
