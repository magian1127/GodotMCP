using System.Security.Cryptography;

namespace GodotMcp.Daemon;

/// <summary>HTTP 面的机器级稳定 token(ADR-0004):首次生成,存于状态目录,跨重启保持稳定。</summary>
public static class DaemonToken
{
    /// <summary>状态目录下的 token 文件名(无扩展名)。</summary>
    public const string TokenFileName = "daemon-token";

    /// <summary>
    /// 确保状态目录内存在稳定 token,存在即复用,不存在则生成 —— 同一台机器上的
    /// 拉起方(如 DaemonProcess)凭同一 token 通过 HTTP 门禁。
    /// </summary>
    /// <param name="stateDir">机器级状态目录,与单例锁同目录。</param>
    /// <returns>token 十六进制字符串(64 字符)。</returns>
    /// <para>逻辑链:创建目录 → 已有非空 token 文件则直接读取返回(Trim 去空白)→
    /// 否则用 <see cref="RandomNumberGenerator"/> 生成 32 字节十六进制 token →
    /// temp+rename 原子写盘后返回;中途崩溃不会留下半截 token 破坏跨重启稳定。</para>
    public static string EnsureStable(string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        var tokenPath = Path.Combine(stateDir, TokenFileName);
        if (File.Exists(tokenPath))
        {
            var existing = File.ReadAllText(tokenPath).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var token = RandomNumberGenerator.GetHexString(32);
        // temp+rename 原子写:中途崩溃不会留下半截 token 文件破坏"跨重启稳定"。
        var tempPath = tokenPath + ".tmp";
        File.WriteAllText(tempPath, token + Environment.NewLine);
        File.Move(tempPath, tokenPath, overwrite: true);
        return token;
    }
}
