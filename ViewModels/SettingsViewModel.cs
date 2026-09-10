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
        OnPropertyChanged(nameof(ThemeIndex)); // 与 Theme 赋值保持一致，确保索引计算属性就绪
        _initializing = false;
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
        _model.DefaultFolder = DefaultFolder;
        _model.Language = Language;
        _model.ZhipuApiKey = ZhipuApiKey;
        _model.QwenApiKey = QwenApiKey;
        _model.NvidiaApiKey = NvidiaApiKey;
        _model.CustomApiUrl = CustomApiUrl;
        _model.CustomApiModel = CustomApiModel;
        _model.CustomApiKey = CustomApiKey;

        await _settings.SaveAsync(_model);
        StatusText = "设置已保存。";
        StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success; // P1-1：保存成功
        StatusBarOpen = true;
    }
}
