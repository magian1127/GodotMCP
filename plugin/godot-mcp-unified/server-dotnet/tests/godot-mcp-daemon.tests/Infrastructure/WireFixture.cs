using System.Text.Json;

namespace GodotMcp.Daemon.Tests.Infrastructure;

/// <summary>
/// 02 号语料(schema wire-fixture/1)的 C# 侧加载器 —— 与 Node harness 共享同一批
/// JSON 文件(真源唯一)。此处只建模 fake Godot 回放所需的子集:auth、onConnect、
/// steps(call / concurrentCalls / sleepMs)与 expectNotifications;断言匹配器
/// ($regex / $untrusted)由 <see cref="JsonMatch"/> 实现。
/// </summary>
internal sealed class WireFixture : IDisposable
{
    /// <summary>夹具 id(与文件名一致,由 LoadAll 校验)。</summary>
    public required string Id { get; init; }

    /// <summary>夹具标题(语料展示名,不参与匹配)。</summary>
    public required string Title { get; init; }

    /// <summary>整份语料 JSON 文档(Dispose 时释放)。</summary>
    public required JsonDocument Document { get; init; }

    /// <summary>语料根对象(以下投影属性的取值入口)。</summary>
    private JsonElement Root => Document.RootElement;

    /// <summary>auth 对象:{ack: {...}} 或 {close: {code, reason}};缺省按 editor ack 处理。</summary>
    public JsonElement Auth => Root.TryGetProperty("auth", out var a) ? a : default;

    /// <summary>onConnect 推送数组(鉴权成功后主动推送的信封帧序列);缺省为 null(无推送)。</summary>
    public JsonElement? OnConnect =>
        Root.TryGetProperty("onConnect", out var o) && o.ValueKind == JsonValueKind.Array ? o : null;

    /// <summary>steps 数组(依序回放的 call / concurrentCalls / sleepMs 步骤)。</summary>
    public JsonElement Steps => Root.GetProperty("steps");

    /// <summary>expectNotifications 数组(期望收到的通知匹配器列表);缺省为 null(不断言通知)。</summary>
    public JsonElement? ExpectNotifications =>
        Root.TryGetProperty("expectNotifications", out var e) ? e : null;

    /// <summary>
    /// 加载目录下全部 *.json 语料(按文件名序)。
    /// 逻辑链:逐文件解析 → schema 非 "wire-fixture/1" 抛出 → id 与文件名不一致抛出 →
    /// 构造夹具列表返回(xunit theory 的数据源)。
    /// </summary>
    /// <param name="dir">语料目录(即 TestPaths.WireFixturesDir)。</param>
    /// <returns>按文件名排序的夹具列表。</returns>
    public static IReadOnlyList<WireFixture> LoadAll(string dir)
    {
        var list = new List<WireFixture>();
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(file));
            var id = Path.GetFileNameWithoutExtension(file);
            if (doc.RootElement.GetProperty("schema").GetString() != "wire-fixture/1")
            {
                throw new InvalidOperationException($"{file}: schema 必须是 wire-fixture/1");
            }
            if (doc.RootElement.GetProperty("id").GetString() != id)
            {
                throw new InvalidOperationException($"{file}: id 必须与文件名一致");
            }
            list.Add(new WireFixture
            {
                Id = id,
                Title = doc.RootElement.GetProperty("title").GetString() ?? "",
                Document = doc,
            });
        }
        return list;
    }

    /// <summary>释放底层 JSON 文档(IDisposable 语义透传)。</summary>
    public void Dispose() => Document.Dispose();
}
