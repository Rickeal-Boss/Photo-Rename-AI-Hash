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

    /// <summary>由页面注入：重命名实际执行前弹出备份文件夹选择。返回 null 表示用户取消。</summary>
    public Func<Task<string?>>? BackupFolderPicker { get; set; }

    public OrganizeViewModel()
    {
        _model = _settings.Load();
        SourceFolder = _model.DefaultFolder;
        OutputFolder = _model.OutputFolder;
        NamingTemplate = _model.NamingTemplate;
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

    [ObservableProperty] private bool _dryRun;

    [ObservableProperty] private bool _useExifDate = true;

    [ObservableProperty] private string _backupFolder = "";

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool _isPaused;

    [ObservableProperty] private string _pauseButtonText = "暂停";

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

        // 重命名模式 + 实际执行：必须先选择备份文件夹，未选择则取消（不动任何文件）
        if ((OperationMode)OperationModeIndex == OperationMode.Rename && !DryRun)
        {
            if (string.IsNullOrWhiteSpace(BackupFolder))
            {
                var picked = BackupFolderPicker != null ? await BackupFolderPicker() : null;
                if (string.IsNullOrWhiteSpace(picked))
                {
                    StatusText = "已取消：重命名实际执行前需先选择备份文件夹。";
                    AppendLog("已取消：未选择备份文件夹，未执行重命名。");
                    return;
                }
                BackupFolder = picked!;
            }
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
            IsPaused = false;
            PauseButtonText = "暂停";
            BackupFolder = ""; // 下次执行重新弹窗让用户确认备份位置
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
            IsPaused = false;
            PauseButtonText = "暂停";
            BackupFolder = ""; // 下次执行重新弹窗让用户确认备份位置
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void PauseToggle()
    {
        if (!IsBusy) return;
        if (IsPaused)
        {
            AppServices.OrganizeService.Resume();
            IsPaused = false;
            PauseButtonText = "暂停";
            StatusText = "继续处理…";
            AppendLog("已继续。");
        }
        else
        {
            AppServices.OrganizeService.Pause();
            IsPaused = true;
            PauseButtonText = "继续";
            StatusText = "已暂停，点击「继续」恢复处理。";
            AppendLog("已暂停。");
        }
    }

    private OrganizeRequest BuildRequest()
    {
        _model = _settings.Load(); // 单例 VM 可能滞后于「设置」页改动，每次构建请求时刷新密钥/端点
        var provider = (AiProvider)AiProviderIndex;
        string key = provider switch
        {
            AiProvider.Zhipu => _model.ZhipuApiKey,
            AiProvider.Qwen => _model.QwenApiKey,
            AiProvider.Custom => _model.CustomApiKey,
            _ => "",
        };

        return new OrganizeRequest
        {
            SourceFolder = SourceFolder,
            OutputFolder = OutputFolder,
            Mode = (OperationMode)OperationModeIndex,
            NamingTemplate = NamingTemplate,
            Conflict = (ConflictStrategy)ConflictIndex,
            DryRun = DryRun,
            AiProvider = provider,
            AiApiKey = key,
            CustomApiUrl = _model.CustomApiUrl,
            CustomApiModel = _model.CustomApiModel,
            Language = _model.Language,
            UseExifDate = UseExifDate,
            BackupFolder = BackupFolder,
        };
    }

    private async Task PersistConfigAsync()
    {
        _model.DefaultFolder = SourceFolder;
        _model.OutputFolder = OutputFolder;
        _model.NamingTemplate = NamingTemplate;
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
        // 限制界面集合增长：超出上限时滚动丢弃最旧一条，始终保留最近 MaxResults 条；
        // 完整结果仍在服务端的 report.Results 与输出目录 rename_log.csv 中。
        if (p.Result != null)
        {
            if (Results.Count >= MaxResults) Results.RemoveAt(0);
            Results.Add(p.Result);
        }
    }

    private const int MaxResults = 2000;

    private void AppendLog(string line) => LogText += line + "\n";
}
