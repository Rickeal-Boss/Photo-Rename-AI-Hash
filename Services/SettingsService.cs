using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoRenameAIHash", "settings.json");

    /// <summary>A-08：整理任务结束的持久化与用户点「保存设置」可能并发，固定 tmp 名会互相争用，
    /// 加进程内串行锁保证同一时刻只有一次 SaveAsync 在写盘。</summary>
    private static readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>
    /// 最近一次 <see cref="Load"/> 是否出现过「值是 enc: 密文但本机凭据解不开」的密钥字段。
    /// 仅进程内状态，不写入 settings.json、也不新增 <see cref="AppSettings"/> 字段（设置字段有兼容红线），
    /// 用于 UI 提示用户重新填写：解密失败时字段已被置空，不明示的话用户会以为「从没填过」。
    /// </summary>
    public bool LastLoadHadUndecryptableKeys { get; private set; }

    public AppSettings Load()
    {
        LastLoadHadUndecryptableKeys = false; // 标志只反映最近一次 Load，每次加载前先重置
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    // 透明解密：密钥字段若以 "enc:" 前缀存储则还原为明文，供 UI 展示/编辑
                    settings.ZhipuApiKey = Unprotect(settings.ZhipuApiKey, out var zhipuFailed);
                    settings.QwenApiKey = Unprotect(settings.QwenApiKey, out var qwenFailed);
                    settings.NvidiaApiKey = Unprotect(settings.NvidiaApiKey, out var nvidiaFailed);
                    settings.CustomApiKey = Unprotect(settings.CustomApiKey, out var customFailed);
                    LastLoadHadUndecryptableKeys = zhipuFailed || qwenFailed || nvidiaFailed || customFailed;
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupted file -> fall back to defaults.
        }

        return new AppSettings();
    }

    /// <summary>
    /// 原子写 settings.json。A-08：整理任务结束的持久化与用户点「保存设置」可能并发，
    /// 固定 tmp 名会互相争用（一方 File.Move 失败被上层 catch 显示为「出错」）。加进程内串行锁。
    /// </summary>
    public async Task SaveAsync(AppSettings settings)
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);

            // 加密仅作用于落盘内容：先在共享对象上暂存密文、序列化、写盘，再还原明文，
            // 避免污染内存中 VM 持有的 settings（TextBox 仍需显示明文）。
            // 保险：若字段值仍是 "enc:" 密文形态（说明上一次解密失败、上层把原文当明文传了回来），
            // 视为无效并写空串——否则 Protect 会对它二次加密，此后只解一层仍得到 "enc:…"，密钥永久不可恢复。
            var z = ScrubCipherText(settings.ZhipuApiKey);
            var q = ScrubCipherText(settings.QwenApiKey);
            var n = ScrubCipherText(settings.NvidiaApiKey);
            var c = ScrubCipherText(settings.CustomApiKey);
            settings.ZhipuApiKey = Protect(z);
            settings.QwenApiKey = Protect(q);
            settings.NvidiaApiKey = Protect(n);
            settings.CustomApiKey = Protect(c);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            // 还原内存对象为明文
            settings.ZhipuApiKey = z;
            settings.QwenApiKey = q;
            settings.NvidiaApiKey = n;
            settings.CustomApiKey = c;

            // 原子写：先写临时文件再原地替换，避免写入中途崩溃损坏 settings.json
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// 用 DPAPI（CurrentUser 作用域）加密敏感字段，密文以 "enc:" 前缀标记以便读取时识别。
    /// 加密失败则降级为明文返回，绝不阻断保存。密钥与当前 Windows 用户账户绑定，
    /// 其他用户/其他机器无法解密，避免 API Key 以明文落盘被任意进程读取。
    /// </summary>
    private static readonly byte[] Entropy =
    {
        0x50, 0x68, 0x6F, 0x74, 0x6F, 0x52, 0x65, 0x6E, 0x61, 0x6D,
        0x65, 0x41, 0x49, 0x48, 0x61, 0x73, 0x68,
    };

    /// <summary>密文前缀：用于识别「该字段已加密」，读取时据此决定是否解密。</summary>
    private const string EncPrefix = "enc:";

    private static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain ?? "";
        try
        {
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return EncPrefix + Convert.ToBase64String(cipher);
        }
        catch
        {
            return plain ?? "";
        }
    }

    /// <summary>
    /// 若值仍是 "enc:" 密文形态，说明它不是用户填的明文而是上一轮解密失败的残留，视为无效并置空。
    /// 这样即使上层原样回传，也不会被 Protect 二次加密。
    /// </summary>
    private static string ScrubCipherText(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(EncPrefix, StringComparison.Ordinal) ? "" : value ?? "";

    /// <summary>
    /// DPAPI 解密。<paramref name="failed"/> 表示「值是 enc: 密文但本机凭据解不开」。
    /// </summary>
    /// <remarks>
    /// 解密失败必须返回<b>空串</b>而不是原值：返回 "enc:…" 原文会让上层把它当成有效密钥，
    /// ① 设置页显示乱码；② 用户点保存 → Protect 对 "enc:…" 再加密一次 → 下次只解一层仍得到
    /// "enc:…"，密钥永久卡死只能手动清空重填；③ 直接跑整理会把 "enc:…" 当 API Key 发出 → 401。
    /// </remarks>
    private static string Unprotect(string? value, out bool failed)
    {
        failed = false;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(EncPrefix, StringComparison.Ordinal))
            return value ?? "";
        try
        {
            var cipher = Convert.FromBase64String(value.Substring(EncPrefix.Length));
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // 解密失败（密钥已不可恢复 / settings.json 从其它账户或机器拷来）：置空并上报，由 UI 提示重填
            failed = true;
            return "";
        }
    }
}
