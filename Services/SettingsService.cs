using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoRenameAIHash", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    // 透明解密：密钥字段若以 "enc:" 前缀存储则还原为明文，供 UI 展示/编辑
                    settings.ZhipuApiKey = Unprotect(settings.ZhipuApiKey);
                    settings.QwenApiKey = Unprotect(settings.QwenApiKey);
                    settings.CustomApiKey = Unprotect(settings.CustomApiKey);
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

    public Task SaveAsync(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);

        // 加密仅作用于落盘内容：先在共享对象上暂存密文、序列化、写盘，再还原明文，
        // 避免污染内存中 VM 持有的 settings（TextBox 仍需显示明文）。
        var z = settings.ZhipuApiKey;
        var q = settings.QwenApiKey;
        var c = settings.CustomApiKey;
        settings.ZhipuApiKey = Protect(z);
        settings.QwenApiKey = Protect(q);
        settings.CustomApiKey = Protect(c);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        // 还原内存对象为明文
        settings.ZhipuApiKey = z;
        settings.QwenApiKey = q;
        settings.CustomApiKey = c;

        // 原子写：先写临时文件再原地替换，避免写入中途崩溃损坏 settings.json
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
        return Task.CompletedTask;
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

    private static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain ?? "";
        try
        {
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return "enc:" + Convert.ToBase64String(cipher);
        }
        catch
        {
            return plain ?? "";
        }
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("enc:", StringComparison.Ordinal))
            return value ?? "";
        try
        {
            var cipher = Convert.FromBase64String(value.Substring(4));
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            // 解密失败（如密钥已不可恢复 / 文件被挪到其它账户）：退回原值，由调用方决定
            return value ?? "";
        }
    }
}
