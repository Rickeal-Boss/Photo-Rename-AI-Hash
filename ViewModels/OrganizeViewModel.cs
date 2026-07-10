using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoRenameAIHash.Models;
using PhotoRenameAIHash.Services;

namespace PhotoRenameAIHash.ViewModels;

public partial class OrganizeViewModel : ObservableObject
{
    private readonly ISettingsService _settings = AppServices.SettingsService;
    private AppSettings _model;
    private CancellationTokenSource? _cts;

    public OrganizeViewModel()
    {
        _model = _settings.Load();
        SourceFolder = _model.DefaultFolder;
        OutputFolder = _model.OutputFolder;
        NamingTemplate = _model.NamingTemplate;
        EnableDedup = _model.EnableDedup;
        AHashThreshold = _model.AHashThreshold;
        DHashThreshold = _model.DHashThreshold;
        DryRun = _model.DryRun;
        UseExifDate = _model.UseExifDate;
        AiProviderIndex = (int)_model.AiProvider;
        OperationModeIndex = (int)_model.OperationMode;
        ConflictIndex = (int)_model.ConflictStrategy;
    }

    [ObservableProperty] private string _sourceFolder = "";

    [ObservableProperty] private string _outputFolder = "";

    [ObservableProperty] private int _aiProviderIndex;

    [ObservableProperty] private string _namingTemplate = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    [ObservableProperty] private int _operationModeIndex;

    [ObservableProperty] private int _conflictIndex;

    [ObservableProperty] private bool _enableDedup;

    [ObservableProperty] private int _aHashThreshold = 8;

    [ObservableProperty] private int _dHashThreshold = 10;

    [ObservableProperty] private bool _dryRun;

    [ObservableProperty] private bool _useExifDate = true;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private int _progress;

    [ObservableProperty] private string _statusText = "选择文件夹后点击「开始整理」。";

    [ObservableProperty] private string _logText = "";

    [ObservableProperty] private ObservableCollection<RenameLogEntry> _results = new();

    [RelayCommand]
    private async Task StartOrganizeAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(SourceFolder) || !Directory.Exists(SourceFolder))
        {
            StatusText = "请选择有效源文件夹。";
            return;
        }

        if (OperationModeIndex != (int)OperationMode.Rename &&
            (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder)))
        {
            StatusText = "请选择有效输出文件夹（重命名模式可留空）。";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Results.Clear();
        LogText = "";
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var req = BuildRequest();
            var progress = new Progress<OrganizeProgress>(OnProgress);
            await AppServices.OrganizeService.RunAsync(req, progress, cts.Token);
            await PersistConfigAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
            AppendLog("已取消。");
        }
        catch (System.Exception ex)
        {
            StatusText = "出错：" + ex.Message;
            AppendLog("出错：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task ArchiveByDateAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(SourceFolder) || !Directory.Exists(SourceFolder))
        {
            StatusText = "请选择有效源文件夹。";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
        {
            StatusText = "请选择有效输出文件夹。";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Results.Clear();
        LogText = "";
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var req = BuildRequest();
            var progress = new Progress<OrganizeProgress>(OnProgress);
            await AppServices.OrganizeService.ArchiveByDateAsync(req, progress, cts.Token);
            await PersistConfigAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
            AppendLog("已取消。");
        }
        catch (System.Exception ex)
        {
            StatusText = "出错：" + ex.Message;
            AppendLog("出错：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task ClearDedupAsync()
    {
        await AppServices.RenameLogService.ClearDedupLibraryAsync();
        StatusText = "已清空去重库。";
        AppendLog("已清空去重库。");
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private OrganizeRequest BuildRequest()
    {
        var provider = (AiProvider)AiProviderIndex;
        string key = provider switch
        {
            AiProvider.Zhipu => _model.ZhipuApiKey,
            AiProvider.Qwen => _model.QwenApiKey,
            _ => "",
        };

        return new OrganizeRequest
        {
            SourceFolder = SourceFolder,
            OutputFolder = OutputFolder,
            Mode = (OperationMode)OperationModeIndex,
            NamingTemplate = NamingTemplate,
            Conflict = (ConflictStrategy)ConflictIndex,
            EnableDedup = EnableDedup,
            AHashThreshold = AHashThreshold,
            DHashThreshold = DHashThreshold,
            DryRun = DryRun,
            AiProvider = provider,
            AiApiKey = key,
            Language = _model.Language,
            UseExifDate = UseExifDate,
        };
    }

    private async Task PersistConfigAsync()
    {
        _model.DefaultFolder = SourceFolder;
        _model.OutputFolder = OutputFolder;
        _model.NamingTemplate = NamingTemplate;
        _model.EnableDedup = EnableDedup;
        _model.AHashThreshold = AHashThreshold;
        _model.DHashThreshold = DHashThreshold;
        _model.DryRun = DryRun;
        _model.UseExifDate = UseExifDate;
        _model.AiProvider = (AiProvider)AiProviderIndex;
        _model.OperationMode = (OperationMode)OperationModeIndex;
        _model.ConflictStrategy = (ConflictStrategy)ConflictIndex;
        await _settings.SaveAsync(_model);
    }

    private void OnProgress(OrganizeProgress p)
    {
        if (!string.IsNullOrEmpty(p.Message)) StatusText = p.Message;
        if (!string.IsNullOrEmpty(p.LogLine))
        {
            LogText += p.LogLine + "\n";
            if (LogText.Length > 20000) LogText = LogText.Substring(LogText.Length - 20000);
        }

        Progress = p.Percent;
        if (p.Result != null) Results.Add(p.Result);
    }

    private void AppendLog(string line) => LogText += line + "\n";
}
