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

    public SettingsViewModel()
    {
        _model = _settings.Load();
        Theme = _model.Theme;
        DefaultFolder = _model.DefaultFolder;
        Language = _model.Language;
        AiProviderIndex = (int)_model.AiProvider;
        ZhipuApiKey = _model.ZhipuApiKey;
        QwenApiKey = _model.QwenApiKey;
        NvidiaApiKey = _model.NvidiaApiKey;
        CustomApiUrl = _model.CustomApiUrl;
        CustomApiModel = _model.CustomApiModel;
        CustomApiKey = _model.CustomApiKey;
        CustomApiRpmLimit = _model.CustomApiRpmLimit;
        OnPropertyChanged(nameof(ThemeIndex)); // 与 Theme 赋值保持一致，确保索引计算属性就绪
        _initializing = false;

        ShowUndecryptableKeysWarningIfNeeded();
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
            _model.AiProvider = (AiProvider)value;
            OnPropertyChanged(nameof(IsCustomProvider));
            OnPropertyChanged(nameof(CustomProviderVisibility));
        }
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
        StatusText = "主题已切换（下次启动也会保留）。";
        StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational; // P1-1：非保存终态用中性提示
        StatusBarOpen = true;
        OnPropertyChanged(nameof(ThemeIndex));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        StatusBarOpen = false; // 先收起，避免用户手动关闭后同值赋值不再触发弹出
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

        await _settings.SaveAsync(_model);
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
        DefaultFolder = _settings.Load().DefaultFolder;
        // 设置页整会话只构造一次，解密失败提示要跟着最新的磁盘状态刷新（用户重填保存后不再提示）
        ShowUndecryptableKeysWarningIfNeeded();
    }
}
