using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public sealed class SettingsService : ISettingsService
{
    /// <summary>
    /// 配置文件路径：<c>%USERPROFILE%\.PhotoRenameAIHash\settings.json</c>。
    /// 刻意放在用户 Profile 根目录下而不是 AppData：MSIX 会对 <c>%LOCALAPPDATA%</c> /
    /// <c>%APPDATA%</c> 做写虚拟化并把它们纳入包数据，<b>卸载 / 重置应用时会被系统一并删除</b>；
    /// Profile 根目录不参与写虚拟化，卸载后配置与 API Key 仍然保留。
    /// </summary>
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".PhotoRenameAIHash", "settings.json");

    /// <summary>旧版配置路径（<c>%LOCALAPPDATA%\PhotoRenameAIHash\settings.json</c>）：仅用于一次性迁出，不再写入。</summary>
    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoRenameAIHash", "settings.json");

    /// <summary>A-08：整理任务结束的持久化与用户点「保存设置」可能并发，固定 tmp 名会互相争用，
    /// 加进程内串行锁保证同一时刻只有一次 SaveAsync 在写盘。</summary>
    private static readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>
    /// 反序列化 settings.json 时的宽松选项。
    /// <b>NumberHandling = AllowReadingFromString</b>：老版本 / 手工编辑 / 第三方工具常把数字写成
    /// 字符串（如 <c>"CustomApiRpmLimit": "30"</c>），严格策略会整份反序列化失败 → 配置静默归零。
    /// 允许从字符串读数字即可消掉这一类最常见的失败（这是 BCL 标准选项，不改变写出格式）。
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 最近一次 <see cref="Load"/> 是否出现过「值是 enc: 密文但本机凭据解不开」的密钥字段。
    /// 仅进程内状态，不写入 settings.json、也不新增 <see cref="AppSettings"/> 字段（设置字段有兼容红线），
    /// 用于 UI 提示用户重新填写：解密失败时字段已被置空，不明示的话用户会以为「从没填过」。
    /// </summary>
    public bool LastLoadHadUndecryptableKeys { get; private set; }

    /// <summary>
    /// A-11：最近一次 <see cref="Load"/> 是否失败（JSON 损坏 / 某个字段类型不匹配 / 读文件异常）。
    /// 失败时返回的是<b>整份归零的默认配置</b>（含 4 个密钥），上层下一次保存就会把它覆盖写回磁盘——
    /// 所以必须让调用方知道，不能像以前那样静默吞掉。
    /// </summary>
    public bool LoadFailedFromDisk { get; private set; }

    /// <summary>A-11：失败原因（异常消息），供 UI 一并展示。</summary>
    public string LastLoadError { get; private set; } = "";

    /// <summary>
    /// A-11：损坏文件的备份路径（同目录下 <c>settings.corrupted.&lt;yyyyMMddHHmmss&gt;.json</c>）。
    /// 备份在 <b>读到失败的那一刻</b>完成，早于任何一次可能的覆盖写。
    /// </summary>
    public string LastCorruptedBackupPath { get; private set; } = "";

    /// <summary>A-11：用户是否已确认过这次损坏（在设置页点「保存设置」即视为确认）。确认后释放保护闩。</summary>
    private bool _loadFailureAcknowledged;

    /// <summary>
    /// A-11：损坏保护闩 —— Load 失败且用户尚未确认时为真。为真期间<b>任何保存都不写盘</b>，
    /// 避免用归零的默认配置把还没确认的损坏文件顶掉（备份只是兜底，能不覆盖就不覆盖）。
    /// </summary>
    public bool HasUnacknowledgedLoadFailure => LoadFailedFromDisk && !_loadFailureAcknowledged;

    /// <summary>
    /// A-11：用户已看到提示并明确要求写入（点「保存设置」）后调用，释放保护闩。
    /// 只在设置页的显式保存入口调用；整理结束的自动持久化不调用。
    /// </summary>
    public void AcknowledgeLoadFailure() => _loadFailureAcknowledged = true;

    /// <summary>
    /// A-11：重新上闩 —— 仅在「写盘失败」时调用。
    /// 确认动作发生在写盘<b>之前</b>（否则 <see cref="SaveAsync"/> 的闩会先抛异常，用户永远无法从设置页保存），
    /// 所以写盘失败必须把确认收回去：否则一次失败的保存就永久消费掉用户的确认，此后
    /// <see cref="HasUnacknowledgedLoadFailure"/> 恒为 false，保护闩彻底失效，
    /// 整理结束的自动持久化会把「整份归零的默认配置（4 个密钥全空）」写回磁盘，顶掉已备份的损坏文件。
    /// </summary>
    public void ReArmLoadFailure() => _loadFailureAcknowledged = false;

    public AppSettings Load()
    {
        LastLoadHadUndecryptableKeys = false; // 标志只反映最近一次 Load，每次加载前先重置
        LoadFailedFromDisk = false;
        LastLoadError = "";
        TryMigrateFromLegacyPath();           // 首次启动时把旧版配置迁到新路径（失败静默）

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                // 内容为空 / 是字面量 null 也按失败处理：此时同样会退回默认配置并可能被覆盖
                var settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions)
                               ?? throw new InvalidDataException("settings.json 内容为空或不是有效的配置对象。");
                // 本次读通了：清掉上一轮的备份路径，之后若再次损坏才会重新备份
                LastCorruptedBackupPath = "";
                // 透明解密：密钥字段若以 "enc:" 前缀存储则还原为明文，供 UI 展示/编辑
                settings.ZhipuApiKey = Unprotect(settings.ZhipuApiKey, out var zhipuFailed);
                settings.QwenApiKey = Unprotect(settings.QwenApiKey, out var qwenFailed);
                settings.NvidiaApiKey = Unprotect(settings.NvidiaApiKey, out var nvidiaFailed);
                settings.CustomApiKey = Unprotect(settings.CustomApiKey, out var customFailed);
                LastLoadHadUndecryptableKeys = zhipuFailed || qwenFailed || nvidiaFailed || customFailed;
                return settings;
            }
        }
        catch (Exception ex)
        {
            // A-11：读取 / 反序列化失败。旧实现直接返回一份全新默认 AppSettings——含 4 个密钥在内
            // 全部归零，而上层下一次保存会把这份空配置整份覆盖写回磁盘，原文件不可恢复且用户毫无察觉。
            // 现在：① 先把原文件备份；② 再逐字段抢救能读出来的部分；③ 置位失败标志交由 UI 出声提示。
            BackupCorruptedFile(ex);

            try
            {
                var salvaged = new AppSettings();
                TrySalvageFields(File.ReadAllText(FilePath), salvaged);
                // 抢救出来的密钥同样落盘为密文，这里一并解密（解密失败由 UI 另行提示重填）
                salvaged.ZhipuApiKey = Unprotect(salvaged.ZhipuApiKey, out var zf);
                salvaged.QwenApiKey = Unprotect(salvaged.QwenApiKey, out var qf);
                salvaged.NvidiaApiKey = Unprotect(salvaged.NvidiaApiKey, out var nf);
                salvaged.CustomApiKey = Unprotect(salvaged.CustomApiKey, out var cf);
                LastLoadHadUndecryptableKeys = zf || qf || nf || cf;
                return salvaged;
            }
            catch
            {
                // 连抢救都失败：返回默认配置。原文件已备份，且保护闩会在用户确认前阻止覆盖写入。
            }
        }

        return new AppSettings();
    }

    /// <summary>
    /// A-11：把读不出来的 settings.json 复制一份到同目录后再返回默认配置。
    /// <b>这是覆盖写回前的最后一道保险</b>——必须在 Load 失败的当下就做，不能等到保存时。
    /// <list type="bullet">
    /// <item>同一份损坏文件<b>只备份一次</b>：Load 在每次导航 / 保存时都会被调，
    /// 不判重会刷出一堆时间戳备份。</item>
    /// <item>备份失败也<b>不抛</b>：备份是兜底，抛出去会让设置页直接不可用；
    /// 只把失败原因并入 <see cref="LastLoadError"/> 交给 UI 展示。</item>
    /// </list>
    /// </summary>
    private void BackupCorruptedFile(Exception ex)
    {
        LoadFailedFromDisk = true;
        LastLoadError = ex.Message;

        // 已有备份且备份文件还在 → 视为同一份损坏文件，不重复备份
        if (!string.IsNullOrEmpty(LastCorruptedBackupPath) && File.Exists(LastCorruptedBackupPath)) return;

        try
        {
            if (!File.Exists(FilePath)) return; // 文件根本不存在（全新安装）：没有可备份的东西
            var dir = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backup = Path.Combine(dir, "settings.corrupted." + stamp + ".json");
            File.Copy(FilePath, backup, overwrite: false);
            LastCorruptedBackupPath = backup;
        }
        catch (Exception backupEx)
        {
            LastLoadError = ex.Message + "（备份失败：" + backupEx.Message + "）";
        }
    }

    /// <summary>
    /// A-11：整份反序列化失败后的<b>逐字段抢救</b>——把还能读出来的字段逐个填回，读不出的保持默认。
    /// 目的：JSON 里某一个字段类型不对时，不至于让「含 4 个密钥在内」的整份配置一起归零。
    /// <b>整段包 try/catch、且只做「读得出才赋值」</b>：抢救本身绝不能再抛（失败路径已经够糟了）。
    /// </summary>
    private static void TrySalvageFields(string json, AppSettings target)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // 属性名与 JsonSerializer 写出的名字一致（未启用命名策略，即 PascalCase 的声明名）
            target.Theme = (AppTheme)ReadInt(root, nameof(AppSettings.Theme), (int)target.Theme);
            target.DefaultFolder = ReadString(root, nameof(AppSettings.DefaultFolder), target.DefaultFolder);
            target.Language = ReadString(root, nameof(AppSettings.Language), target.Language);
            target.OperationMode = (OperationMode)ReadInt(root, nameof(AppSettings.OperationMode), (int)target.OperationMode);
            target.NamingTemplate = ReadString(root, nameof(AppSettings.NamingTemplate), target.NamingTemplate);
            target.ConflictStrategy = (ConflictStrategy)ReadInt(root, nameof(AppSettings.ConflictStrategy), (int)target.ConflictStrategy);
            target.OutputFolder = ReadString(root, nameof(AppSettings.OutputFolder), target.OutputFolder);
            target.DryRun = ReadBool(root, nameof(AppSettings.DryRun), target.DryRun);
            target.UseExifDate = ReadBool(root, nameof(AppSettings.UseExifDate), target.UseExifDate);
            target.AiProvider = (AiProvider)ReadInt(root, nameof(AppSettings.AiProvider), (int)target.AiProvider);
            target.ZhipuApiKey = ReadString(root, nameof(AppSettings.ZhipuApiKey), target.ZhipuApiKey);
            target.QwenApiKey = ReadString(root, nameof(AppSettings.QwenApiKey), target.QwenApiKey);
            target.NvidiaApiKey = ReadString(root, nameof(AppSettings.NvidiaApiKey), target.NvidiaApiKey);
            target.CustomApiUrl = ReadString(root, nameof(AppSettings.CustomApiUrl), target.CustomApiUrl);
            target.CustomApiModel = ReadString(root, nameof(AppSettings.CustomApiModel), target.CustomApiModel);
            target.CustomApiKey = ReadString(root, nameof(AppSettings.CustomApiKey), target.CustomApiKey);
            target.CustomApiRpmLimit = ReadInt(root, nameof(AppSettings.CustomApiRpmLimit), target.CustomApiRpmLimit);
        }
        catch
        {
            // 抢救失败：保持默认，由上层按「整份读取失败」处理（原文件已备份，不会被静默覆盖）
        }
    }

    /// <summary>读字符串字段：只认 String / Number 两种形态，其余（对象、数组、null）保持默认值。</summary>
    private static string ReadString(JsonElement obj, string name, string fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        try
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? fallback,
                JsonValueKind.Number => el.ToString(),
                _ => fallback,
            };
        }
        catch { return fallback; }
    }

    /// <summary>读整数字段：数字直接取；被写成字符串的再 TryParse 一次（与 ReadOptions 的宽松策略一致）。</summary>
    private static int ReadInt(JsonElement obj, string name, int fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
            return fallback;
        }
        catch { return fallback; }
    }

    /// <summary>读布尔字段：兼容 true/false 与 "true"/"false" 字符串写法。</summary>
    private static bool ReadBool(JsonElement obj, string name, bool fallback)
    {
        if (!obj.TryGetProperty(name, out var el)) return fallback;
        try
        {
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : fallback,
                _ => fallback,
            };
        }
        catch { return fallback; }
    }

    /// <summary>
    /// 原子写 settings.json。A-08：整理任务结束的持久化与用户点「保存设置」可能并发，
    /// 固定 tmp 名会互相争用（一方 File.Move 失败被上层 catch 显示为「出错」）。加进程内串行锁。
    /// </summary>
    public async Task SaveAsync(AppSettings settings)
    {
        // A-11：损坏保护闩 —— Load 失败且用户尚未确认时不得写盘。
        // 此时上层手里那份配置是「整份归零的默认值」（含 4 个密钥），写下去就把原文件顶掉了。
        // 备份已在 Load 失败那一刻做完，但能不覆盖就不覆盖；由设置页提示用户确认后再写。
        // 抛而不是静默跳过：静默跳过会让「保存设置」按钮谎报成功（P33）。
        if (HasUnacknowledgedLoadFailure)
        {
            throw new InvalidOperationException("配置文件读取失败" +
                (string.IsNullOrEmpty(LastCorruptedBackupPath) ? "" : "（已备份到 " + LastCorruptedBackupPath + "）") +
                "，为避免覆盖原文件，本次未写入。");
        }

        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // 与 Load 同样先尝试迁移：整理结束的持久化可能早于任何一次 Load，
            // 不迁移的话旧配置会被这次保存直接「顶掉」（新路径被写成当前内存模型）。
            TryMigrateFromLegacyPath();

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
    /// 一次性迁移：把旧版 <c>%LOCALAPPDATA%\PhotoRenameAIHash\settings.json</c> 复制到新路径。
    /// 老用户升级后如果丢配置，等于用一个新的 P0 换掉旧的 P0，所以必须做。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>触发条件：<b>新路径不存在</b> 且 <b>旧路径存在</b>。新路径已存在说明已迁移过或是全新安装，不再动。</item>
    /// <item>只做<b>文件级复制</b>、不解析 JSON：避免与 DPAPI 解密耦合（迁移发生在解密之前），
    /// 也避免反序列化 / 再序列化过程中把读不懂的字段写丢。</item>
    /// <item><b>保留旧文件不删</b>：用 Copy 而不是 Move。万一新路径后续写失败或迁移出的文件损坏，
    /// 用户的原始配置仍在旧位置，可手工取回。</item>
    /// <item>全程 try/catch 且 catch 里<b>不抛、不记录失败状态</b>：迁移失败的最坏结果只是
    /// 「按新配置（默认值）启动」，绝不能导致启动失败或设置不可用。</item>
    /// </list>
    /// </remarks>
    private static void TryMigrateFromLegacyPath()
    {
        try
        {
            if (File.Exists(FilePath) || !File.Exists(LegacyFilePath)) return;

            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // overwrite: false —— 并发（Load 与 SaveAsync 同时首次触发）时后来的那次会抛 IOException，
            // 被下面的 catch 静默吞掉，不会覆盖已迁好的文件
            File.Copy(LegacyFilePath, FilePath, overwrite: false);
        }
        catch
        {
            // 迁移失败（无权限 / 磁盘满 / 文件被占用 / 并发竞争）：静默回退到正常加载流程
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
