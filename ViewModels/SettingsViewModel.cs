using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;
using PhotoRenameAIHash.Services;

namespace PhotoRenameAIHash.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings = AppServices.SettingsService;
    private AppSettings _model;

    public SettingsViewModel()
    {
        _model = _settings.Load();
        Theme = _model.Theme;
        DefaultFolder = _model.DefaultFolder;
        AHashThreshold = _model.AHashThreshold;
        DHashThreshold = _model.DHashThreshold;
        Language = _model.Language;
        AiProviderIndex = (int)_model.AiProvider;
        ZhipuApiKey = _model.ZhipuApiKey;
        QwenApiKey = _model.QwenApiKey;
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

    /// <summary>Maps AiProvider -> ComboBox index (0=None, 1=Zhipu, 2=Qwen).</summary>
    public int AiProviderIndex
    {
        get => (int)_model.AiProvider;
        set => _model.AiProvider = (AiProvider)value;
    }

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private string _defaultFolder = "";

    [ObservableProperty]
    private int _aHashThreshold;

    [ObservableProperty]
    private int _dHashThreshold;

    [ObservableProperty]
    private string _language = "";

    [ObservableProperty]
    private string _zhipuApiKey = "";

    [ObservableProperty]
    private string _qwenApiKey = "";

    [ObservableProperty]
    private string _statusText = "";

    partial void OnThemeChanged(AppTheme oldValue, AppTheme newValue)
    {
        _model.Theme = newValue;
        ThemeHelper.Apply(PhotoRenameAIHash.App.MainWindow!, newValue);
        StatusText = "主题已切换（下次启动也会保留）。";
        OnPropertyChanged(nameof(ThemeIndex));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _model.DefaultFolder = DefaultFolder;
        _model.AHashThreshold = AHashThreshold;
        _model.DHashThreshold = DHashThreshold;
        _model.Language = Language;
        _model.ZhipuApiKey = ZhipuApiKey;
        _model.QwenApiKey = QwenApiKey;

        await _settings.SaveAsync(_model);
        StatusText = "设置已保存。";
    }
}
