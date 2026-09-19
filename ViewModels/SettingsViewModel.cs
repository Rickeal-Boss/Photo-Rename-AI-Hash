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
        _model.CustomApiUrl = CustomApiUrl;
        _model.CustomApiModel = CustomApiModel;
        _model.CustomApiKey = CustomApiKey;
        // NumberBox 清空文本时 Value 为 NaN：按「不限」落盘，避免写入未定义的整数
        var rpm = double.IsNaN(CustomApiRpmLimit) || CustomApiRpmLimit < 0
            ? 0
            : (int)Math.Round(CustomApiRpmLimit);
        _model.CustomApiRpmLimit = rpm;
        // P2-6：取整后的值必须回写 VM，否则页面仍显示 3.7 而磁盘是 4（显示与落盘不一致）
        CustomApiRpmLimit = rpm;

        await _settings.SaveAsync(_model);
        // 通知依赖 _model 派生的计算属性（AiProviderIndex/可见性等）刷新
        OnPropertyChanged(nameof(AiProviderIndex));
        OnPropertyChanged(nameof(CustomProviderVisibility));
        StatusText = "设置已保存。";
        StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success; // P1-1：保存成功
        StatusBarOpen = true;
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
