using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;
using PhotoRenameAIHash.Services;

namespace PhotoRenameAIHash.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings = AppServices.SettingsService;
    private AppSettings _model;

    // P1-2 修复：ctor 中 Theme 赋值会触发 OnThemeChanged，初始化期抑制
    // 「主题已切换」InfoBar 误弹出（仅抑制提示，保留 _model.Theme 同步逻辑）
    private bool _initializing = true;

    /// <summary>
    /// A-11：用户在设置页手动改过「AI 区块」（引擎 / 自定义端点 / 模型 / RPM）且尚未落盘。
    /// 设置页是 NavigationCacheMode="Required"（整会话只构造一次），而 AiProvider 也会被整理页写入；
    /// 导航回本页时要以磁盘值对齐下拉框，但<b>不能把用户没保存的编辑冲掉</b>——故用此标志区分
    /// 「值陈旧（应同步）」与「用户手动改过（不应覆盖）」，与 OrganizeViewModel.SyncProviderFromDisk
    /// 用的是同一套路。
    /// </summary>
    private bool _aiBlockDirty;

    /// <summary>A-11：同步 / 初始化期间置位——由同步引起的属性变化不算「用户手动改过」。</summary>
    private bool _suppressAiDirty;

    public SettingsViewModel()
    {
        _model = _settings.Load();
        Theme = _model.Theme;
        DefaultFolder = _model.DefaultFolder;
        Language = _model.Language;
        // P2-4：System.Text.Json 把数字反序列化成枚举时<b>不校验定义域</b>（settings.json 里写
        // "AiProvider": 99 不会报错），越界值会让 ComboBox 因无匹配项而显示空白。钳到枚举实际范围，
        // 同时 setter 会把钳后的值写回 _model.AiProvider，getter 之后读到的也是合法值。
        AiProviderIndex = Math.Clamp((int)_model.AiProvider, (int)AiProvider.None, (int)AiProvider.Nvidia);
        ZhipuApiKey = _model.ZhipuApiKey;
        QwenApiKey = _model.QwenApiKey;
        NvidiaApiKey = _model.NvidiaApiKey;
        CustomApiUrl = _model.CustomApiUrl;
        CustomApiModel = _model.CustomApiModel;
        CustomApiKey = _model.CustomApiKey;
        CustomApiRpmLimit = _model.CustomApiRpmLimit;
        OnPropertyChanged(nameof(ThemeIndex)); // 与 Theme 赋值保持一致，确保索引计算属性就绪
        _initializing = false;
        _aiBlockDirty = false; // 构造期的赋值由 _initializing 抑制，这里再显式兜一次底

        ShowUndecryptableKeysWarningIfNeeded();
        // A-11：配置读取失败提示（更严重，放在后面以便覆盖上面的解密失败提示）
        ShowLoadFailureWarningIfNeeded();
    }

    /// <summary>
    /// A-11：settings.json 反序列化 / 读取失败时明确告知用户。
    /// 此时 <see cref="ISettingsService.Load"/> 返回的是<b>整份归零的默认配置</b>（含 4 个密钥），
    /// 但原文件已被备份、且在用户点「保存设置」确认之前不会被任何保存覆盖。
    /// 不提示的话用户只会看到「配置莫名其妙空了」，并可能在不知情的情况下把空配置写回去。
    /// </summary>
    /// <remarks>标志只挂在 <see cref="SettingsService"/> 实现上（不进契约、不进 AppSettings），故按具体类型读取。</remarks>
    private void ShowLoadFailureWarningIfNeeded()
    {
        if (_settings is not SettingsService settings) return;
        if (!settings.LoadFailedFromDisk) return;

        var backup = settings.LastCorruptedBackupPath;
        StatusText = "配置文件读取失败" +
                     (string.IsNullOrEmpty(backup) ? "" : "（已备份到 " + backup + "）") +
                     "，为避免覆盖，本次未自动保存；请检查文件或重新配置。" +
                     (string.IsNullOrEmpty(settings.LastLoadError) ? "" : "（原因：" + settings.LastLoadError + "）");
        StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        StatusBarOpen = true;
    }

    /// <summary>
    /// 本机 Windows 凭据解不开已保存的 API Key（settings.json 从其它账户 / 其它机器拷来、或密钥已不可恢复）时提示用户。
    /// 此时 <see cref="ISettingsService.Load"/> 已把这些字段置空——不提示的话用户看到空 Key 会以为「从没填过」，
    /// 而直接开始整理会拿到 401。
    /// </summary>
    /// <remarks>标志只挂在 <see cref="SettingsService"/> 实现上：不进 <see cref="ISettingsService"/> 契约、
    /// 也不进 <see cref="AppSettings"/>（设置字段有兼容红线），故此处按具体类型读取。</remarks>
    private void ShowUndecryptableKeysWarningIfNeeded()
    {
        if (_settings is not SettingsService settings) return;
        if (!settings.LastLoadHadUndecryptableKeys) return;

        StatusText = "本机 Windows 凭据无法解密已保存的 API Key（设置文件可能来自其它账户或机器），已置空，请重新填写后保存。";
        StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
        StatusBarOpen = true;
    }

    /// <summary>Maps AppTheme -> RadioButtons index (0=Light, 1=Dark, 2=System).</summary>
    public int ThemeIndex
    {
        get => Theme switch
        {
            AppTheme.Light => 0,
            AppTheme.Dark => 1,
            _ => 2,
        };
        set => Theme = value switch
        {
            0 => AppTheme.Light,
            1 => AppTheme.Dark,
            _ => AppTheme.System,
        };
    }

    /// <summary>Maps AiProvider -> ComboBox index (0=None, 1=Zhipu, 2=Qwen, 3=Custom, 4=Nvidia).</summary>
    public int AiProviderIndex
    {
        get => (int)_model.AiProvider;
        set
        {
            // A-11：用户在本页改过引擎 → 标记 AI 区块为脏，导航回本页时不再被磁盘值覆盖
            // （构造期赋值与同步期赋值都通过 _initializing / _suppressAiDirty 排除）
            if (!_initializing && !_suppressAiDirty && (int)_model.AiProvider != value) _aiBlockDirty = true;
            _model.AiProvider = (AiProvider)value;
            OnPropertyChanged(nameof(IsCustomProvider));
            OnPropertyChanged(nameof(CustomProviderVisibility));
        }
    }

    // A-11：自定义端点 / 模型 / RPM 同属「设置页拥有的 AI 区块」。用户在设置页改过但未保存时，
    // 导航回本页不得以磁盘值覆盖（否则等于静默丢弃他的编辑——正是本次要修的那类静默回退）。
    partial void OnCustomApiUrlChanged(string value)
    {
        MarkAiBlockDirty();
    }

    partial void OnCustomApiModelChanged(string value)
    {
        MarkAiBlockDirty();
    }

    partial void OnCustomApiRpmLimitChanged(double value)
    {
        MarkAiBlockDirty();
    }

    private void MarkAiBlockDirty()
    {
        if (!_initializing && !_suppressAiDirty) _aiBlockDirty = true;
    }

    /// <summary>当前选中的是否为「自定义」引擎。</summary>
    public bool IsCustomProvider => _model.AiProvider == AiProvider.Custom;

    /// <summary>语言选项索引（P2-8）：0=中文(zh-CN)，1=English(en-US)。替代自由文本，避免无效值。</summary>
    public int LanguageIndex
    {
        get => (Language ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        set
        {
            var v = value < 0 ? 0 : (value > 1 ? 1 : value);
            Language = v == 0 ? "zh-CN" : "en-US";
            OnPropertyChanged(nameof(LanguageIndex));
        }
    }

    /// <summary>自定义引擎区域可见性。</summary>
    public Visibility CustomProviderVisibility =>
        IsCustomProvider ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private string _defaultFolder = "";

    [ObservableProperty]
    private string _language = "";

    [ObservableProperty]
    private string _zhipuApiKey = "";

    [ObservableProperty]
    private string _qwenApiKey = "";

    [ObservableProperty]
    private string _nvidiaApiKey = "";

    [ObservableProperty]
    private string _customApiUrl = "";

    [ObservableProperty]
    private string _customApiModel = "";

    [ObservableProperty]
    private string _customApiKey = "";

    /// <summary>自定义引擎的每分钟请求上限（0 = 不限）。
    /// 类型取 <see cref="double"/>：NumberBox.Value 是 double，x:Bind 不做数值隐式转换，
    /// 绑 int 属性会在 TwoWay 回写时编译失败（double → int 无隐式转换）；落盘时再取整。</summary>
    [ObservableProperty]
    private double _customApiRpmLimit;

    [ObservableProperty]
    private string _statusText = "";

    // P1-1：保存结果以 InfoBar 高亮提示
    [ObservableProperty]
    private bool _statusBarOpen;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Controls.InfoBarSeverity _statusSeverity =
        Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

    partial void OnThemeChanged(AppTheme oldValue, AppTheme newValue)
    {
        // P1-2 修复：初始化期间的 Theme 赋值不是用户切换主题，跳过提示弹出
        if (_initializing) return;

        _model.Theme = newValue;
        var mainWindow = PhotoRenameAIHash.App.MainWindow;
        if (mainWindow != null) ThemeHelper.Apply(mainWindow, newValue);
        OnPropertyChanged(nameof(ThemeIndex));

        // A-11：页面文案写着「切换立即生效并保留 / 下次启动也会保留」，但此前只改内存值不落盘——
        // 用户切完主题不点「保存设置」直接退出，主题就会回退且无从察觉（P33 谎报）。
        // 这里在切换的同时立即持久化；保存结果（含失败）由 SaveThemeAsync 出提示。
        _ = SaveThemeAsync();
    }

    /// <summary>
    /// A-11：主题切换后的即时落盘。只写「主题」这一个本页拥有的字段，不触碰其它字段——
    /// 避免把整理页刚持久化的源/输出文件夹等配置用构造期快照回退。
    /// </summary>
    private async Task SaveThemeAsync()
    {
        // A-11：沿用 SaveAsync 的既有约定——「重读磁盘快照前先捕获 UI 当前值」。
        // AiProviderIndex 的 getter 读 _model.AiProvider，若不先捕获、重读后取到的会是磁盘旧值。
        var pendingProvider = (AiProvider)AiProviderIndex;
        try
        {
            // 重读磁盘（不覆盖 _model，避免打乱本页其余字段的内存状态），只回写主题与引擎
            var model = _settings.Load();
            model.Theme = Theme;
            model.AiProvider = pendingProvider;
            await _settings.SaveAsync(model);

            StatusText = "主题已切换（已保存，下次启动也会保留）。";
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        }
        catch (Exception ex)
        {
            // 保存失败必须出声：否则用户以为主题已保留，下次启动却回退（P33 谎报）
            StatusText = "主题已切换，但保存到本机失败：" + ex.Message + "。下次启动会回退，请点「保存设置」重试。";
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        }
        StatusBarOpen = true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        StatusBarOpen = false; // 先收起，避免用户手动关闭后同值赋值不再触发弹出

        // A-11：用户在设置页点「保存设置」= 明确确认「我接受覆盖那份读不出来的配置文件」
        // （原文件在 Load 失败那一刻已备份，不会不可恢复）。整理结束的自动持久化不调用这里。
        if (_settings is SettingsService settingsImpl && settingsImpl.LoadFailedFromDisk)
            settingsImpl.AcknowledgeLoadFailure();

        // P1-A 修复：_model 是页面构造时的快照（页面 Required 缓存后整会话不刷新），
        // 直接整文件保存会静默回退整理页等其它来源刚写入的配置。保存前先重读磁盘，
        // 只覆盖本页拥有的字段，其余字段（整理配置等）以磁盘最新值为准。
        // 注意：AiProviderIndex 的 getter 读取 _model.AiProvider，必须先在重读前捕获
        // UI 当前选择，否则重读后 getter 会取到磁盘旧值（P0-1 修复）。
        var pendingProvider = (AiProvider)AiProviderIndex;
        _model = _settings.Load();
        _model.Theme = Theme;
        // A-07：DefaultFolder 由整理页拥有（整理运行结束时写入），本页不再回写，
        // 避免用构造时快照把它静默回退；本页仅只读展示，导航回页时由 SyncOwnedFieldsFromDisk 同步。
        _model.Language = Language;
        _model.AiProvider = pendingProvider;
        _model.ZhipuApiKey = ZhipuApiKey;
        _model.QwenApiKey = QwenApiKey;
        _model.NvidiaApiKey = NvidiaApiKey;
        // D-5：CustomApiUrl 不在 DPAPI 加密清单内，是明文落盘的；而 Google 的 OpenAI 兼容端点等
        // 把密钥写在 ?key= 里（Gemini host 嗅探会招徕这种粘贴）。保存前把这类查询参数摘出来：
        // 回填到密钥框（走 DPAPI 加密），并从 URL 移除。解析失败 / 无内嵌密钥 → 原样保存，
        // 绝不因 URL 格式问题阻止用户保存（P24 精神：持久化问题不能让保存按钮静默失效）。
        var urlNote = "";
        var customUrl = StripSecretFromUrl(CustomApiUrl, out var embeddedKey);
        _model.CustomApiUrl = customUrl;
        if (embeddedKey.Length > 0)
        {
            CustomApiUrl = customUrl; // 回写 VM：否则输入框仍显示带密钥的 URL（P33 谎报）
            if (string.IsNullOrWhiteSpace(CustomApiKey))
            {
                CustomApiKey = embeddedKey; // 下方会写入 _model.CustomApiKey → 走 DPAPI
                urlNote = "已从端点 URL 中移出密钥，改存到加密的 API Key 框。";
            }
            else
            {
                urlNote = "已从端点 URL 中移除内嵌密钥（API Key 框已有值，未覆盖）。";
            }
        }

        _model.CustomApiModel = CustomApiModel;
        _model.CustomApiKey = CustomApiKey;
        // P1-D：一次性吃掉全部脏输入 —— NaN（NumberBox 清空文本时 Value 就是 NaN）、±∞、
        // 负数、以及超过 UI 上界 600 的值（此前只钳下界，1e11 会溢出成 int.MinValue 落盘，
        // 下个会话在 Minimum=0 的 NumberBox 里显示 -2147483648 → UI 谎报）。
        // AwayFromZero 是必要的：Math.Round 默认 ToEven 会把 0.5 舍成 0，用户想要 1 却变成「不限」。
        var rpm = double.IsFinite(CustomApiRpmLimit)
            ? (int)Math.Clamp(Math.Round(CustomApiRpmLimit, MidpointRounding.AwayFromZero), 0, 600)
            : 0;
        _model.CustomApiRpmLimit = rpm;
        // P2-6：取整/钳制后的值必须回写 VM，否则页面仍显示 3.7 或 1e11 而磁盘是另一个值
        //（显示与落盘不一致，P33）
        CustomApiRpmLimit = rpm;

        try
        {
            await _settings.SaveAsync(_model);
        }
        catch (Exception ex)
        {
            // A-11：写盘失败必须把上面那次「确认」收回去（重新上闩）。
            // 确认只能发生在写盘之前（见上方注释：放在写盘后会导致 SaveAsync 的闩先抛异常、
            // 用户永远保存不了），所以「确认」只有在真正写盘成功时才算数。此前失败分支只改 StatusText、
            // 不复位 → 一次失败的保存就永久消费掉用户的确认，保护闩此后恒为释放状态，
            // 整理结束的自动持久化会把归零的默认配置写回磁盘，顶掉那份已备份的损坏文件。
            if (_settings is SettingsService saveFailedImpl) saveFailedImpl.ReArmLoadFailure();

            // A-11：保存失败必须出声。此前没有 catch，一旦写盘失败（权限/磁盘满/保护闩）
            // 命令内的异常会被 AsyncRelayCommand 收进 ExecutionTask，界面上什么都不会发生——
            // 用户以为配置已存，下次启动却是旧值（P33 谎报）。
            StatusText = "保存失败：" + ex.Message;
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
            StatusBarOpen = true;
            return;
        }

        // 已落盘 → 本页 AI 区块与磁盘重新对齐，之后的导航同步不会被误判为「用户改过」
        _aiBlockDirty = false;

        // 通知依赖 _model 派生的计算属性（AiProviderIndex/可见性等）刷新
        OnPropertyChanged(nameof(AiProviderIndex));
        OnPropertyChanged(nameof(CustomProviderVisibility));
        StatusText = "设置已保存。" + urlNote;
        StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success; // P1-1：保存成功
        StatusBarOpen = true;
    }

    /// <summary>端点 URL 里承载密钥的查询参数名（大小写不敏感）。</summary>
    private static readonly string[] SecretQueryKeys =
    {
        "key", "api-key", "api_key", "apikey", "access_token", "token",
    };

    /// <summary>
    /// 把端点 URL 查询串里的密钥参数摘出来并从 URL 中移除（保留其它查询参数与锚点）。
    /// 找不到 / 解析失败 → 原样返回输入，<paramref name="secret"/> 为空串。
    /// <b>整段包 try/catch</b>：这是保存路径，绝不能让 URL 解析异常把整个保存搞挂（P24）。
    /// </summary>
    private static string StripSecretFromUrl(string? url, out string secret)
    {
        secret = "";
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return url ?? "";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url;

            var query = uri.Query; // 含前导 '?'；无查询串时为 ""
            if (query.Length <= 1) return url;

            var kept = new List<string>();
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                var name = eq >= 0 ? pair.Substring(0, eq) : pair;
                var value = eq >= 0 ? pair.Substring(eq + 1) : "";
                // 只摘第一个命中的非空值，其余同名参数按普通参数保留（不猜用户意图）
                if (secret.Length == 0 && value.Length > 0 && IsSecretKeyName(name))
                {
                    secret = Uri.UnescapeDataString(value);
                    continue;
                }
                kept.Add(pair);
            }

            if (secret.Length == 0) return url; // 无内嵌密钥：原样

            var newQuery = kept.Count > 0 ? "?" + string.Join("&", kept) : "";
            // GetLeftPart(Path) 只到路径为止（不含查询与锚点），Fragment 自带 '#'
            return uri.GetLeftPart(UriPartial.Path) + newQuery + uri.Fragment;
        }
        catch
        {
            secret = "";
            return url ?? "";
        }
    }

    private static bool IsSecretKeyName(string name)
    {
        var n = name.Trim();
        foreach (var k in SecretQueryKeys)
        {
            if (n.Equals(k, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// A-07：设置页为 Required 缓存、整会话只构造一次，而 <see cref="AppSettings.DefaultFolder"/>
    /// 由整理页拥有（整理运行结束时写入）。每次导航回本页时从磁盘同步该字段，
    /// 避免用陈旧快照把它写回；同时该字段在 UI 上改为只读展示。
    /// </summary>
    public void SyncOwnedFieldsFromDisk()
    {
        var disk = _settings.Load();
        DefaultFolder = disk.DefaultFolder;

        // A-11：此前这里只同步了 DefaultFolder，AI 引擎下拉框不同步 → 用户在整理页改了引擎并持久化后
        // 进设置页看到的仍是旧值，再点「保存设置」就把旧引擎写回去了（用户改的引擎静默丢失）。
        // 现在把设置页拥有的 AI 字段一起与磁盘对齐。
        // 仅在用户本页没手动改过（_aiBlockDirty == false）时覆盖，否则会把他没保存的编辑冲掉。
        if (!_aiBlockDirty)
        {
            _suppressAiDirty = true; // 同步引起的属性变化不算「用户手动改过」
            try
            {
                // P2-4：磁盘值同样可能越界（见构造函数处注释），同步时一并钳制，
                // 否则导航回设置页会把空白下拉框再带回来。
                AiProviderIndex = Math.Clamp((int)disk.AiProvider, (int)AiProvider.None, (int)AiProvider.Nvidia);
                CustomApiUrl = disk.CustomApiUrl;
                CustomApiModel = disk.CustomApiModel;
                CustomApiRpmLimit = disk.CustomApiRpmLimit;
                _aiBlockDirty = false;
            }
            finally
            {
                _suppressAiDirty = false;
            }
            // 下拉框与「自定义」区块可见性都依赖 _model.AiProvider，同步后主动刷新
            OnPropertyChanged(nameof(AiProviderIndex));
            OnPropertyChanged(nameof(IsCustomProvider));
            OnPropertyChanged(nameof(CustomProviderVisibility));
        }

        // 设置页整会话只构造一次，解密失败提示要跟着最新的磁盘状态刷新（用户重填保存后不再提示）
        ShowUndecryptableKeysWarningIfNeeded();
        // A-11：配置读取失败提示（更严重，放在后面以便覆盖上面的解密失败提示）
        ShowLoadFailureWarningIfNeeded();
    }
}
