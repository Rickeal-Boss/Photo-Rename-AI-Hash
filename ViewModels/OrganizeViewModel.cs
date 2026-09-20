using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PhotoRenameAIHash.Models;
using PhotoRenameAIHash.Services;

namespace PhotoRenameAIHash.ViewModels;

public partial class OrganizeViewModel : ObservableObject
{
    private readonly ISettingsService _settings = AppServices.SettingsService;
    private AppSettings _model;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 最近一次「与磁盘对齐」时的引擎索引。用于区分两种「当前值 ≠ 磁盘值」：
    /// 值陈旧（应同步） vs 用户在整理页手动改过（不应覆盖）——见 <see cref="SyncProviderFromDisk"/>。
    /// </summary>
    private int _syncedProviderIndex;

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
        _syncedProviderIndex = AiProviderIndex; // 构造即视为已与磁盘对齐
        OperationModeIndex = (int)_model.OperationMode;
        ConflictIndex = (int)_model.ConflictStrategy;
        // P1-4：结果集合变化时同步空态/列表可见性
        Results.CollectionChanged += (_, __) => UpdateResultVisibility();
        UpdateResultVisibility();
    }

    [ObservableProperty] private string _sourceFolder = "";

    [ObservableProperty] private string _outputFolder = "";

    [ObservableProperty] private int _aiProviderIndex;

    [ObservableProperty] private string _namingTemplate = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    [ObservableProperty] private int _operationModeIndex;

    [ObservableProperty] private int _conflictIndex;

    [ObservableProperty] private bool _dryRun;

    /// <summary>
    /// 运行模式选项索引（P0-2 修复）：0=模拟运行，1=实际执行。
    /// 由 DryRun 派生，供 RadioButtons 双向绑定——控件语义所见即所选，
    /// 彻底消除旧 ToggleSwitch「标签与行为颠倒」的问题。
    /// </summary>
    public int RunModeIndex
    {
        get => DryRun ? 0 : 1;
        set
        {
            var v = value < 0 ? 0 : (value > 1 ? 1 : value); // 越界值钳制到合法选项
            if (DryRun == (v == 0) && RunModeIndex == v) return; // 值未变化时不触发通知
            DryRun = v == 0;
            OnPropertyChanged(nameof(RunModeIndex));
        }
    }

    [ObservableProperty] private bool _useExifDate = true;

    [ObservableProperty] private string _backupFolder = "";

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool _isPaused;

    [ObservableProperty] private string _pauseButtonText = "暂停";

    [ObservableProperty] private int _progress;

    /// <summary>
    /// A-11：进度条是否走不确定态——只在「忙但还没有任何具体进度值」时为 true。
    /// 此前 IsIndeterminate 直接绑 IsBusy，整个运行期都是滚动动画，
    /// 精心算出来的 Percent 用户根本看不到；有具体百分比后必须切到确定态。
    /// </summary>
    public bool IsProgressIndeterminate => IsBusy && Progress <= 0;

    // IsBusy / Progress 任一变化都要让上面的计算属性重新求值（x:Bind OneWay 只认属性变更通知）
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    partial void OnProgressChanged(int value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    [ObservableProperty] private string _statusText = "选择文件夹后点击「开始整理」。";

    /// <summary>
    /// AI 观测行：「AI 实际 X 张/分 · 服务端响应 Y 秒/张 · 闸门上限 Z 张/分」。
    /// 存在的意义是消除「设置里写的 30 RPM 就是目标速率」这一误解——
    /// 三个数并排，能直接看出慢的是服务端还是闸门。空串表示本行不适用（未启用 AI / 样本不足）。
    /// </summary>
    [ObservableProperty] private string _rateText = "";

    /// <summary>观测行的可见性：有内容才占位，避免未启用 AI 时进度区留一行空白。</summary>
    public Visibility RateLineVisibility => RateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // RateText 变化时同步刷新可见性（与 OnStatusBarOpenChanged → StatusLineVisibility 同一套路）
    partial void OnRateTextChanged(string value)
    {
        OnPropertyChanged(nameof(RateLineVisibility));
    }

    [ObservableProperty] private string _logText = "";

    [ObservableProperty] private ObservableCollection<RenameLogEntry> _results = new();

    // ── P1-1：任务终态（完成/失败/取消）以 InfoBar 高亮提示；新任务开始时关闭 ──
    [ObservableProperty] private bool _statusBarOpen;

    /// <summary>InfoBar 专用消息：仅在终态（StatusBarOpen 打开）时与 StatusText 同步，
    /// 与进度区下方常驻的 StatusText 分工——前者是「终态横幅」，后者是「实时状态行」，避免同句重复展示。</summary>
    [ObservableProperty] private string _statusBarMessage = "";

    [ObservableProperty] private Microsoft.UI.Xaml.Controls.InfoBarSeverity _statusSeverity =
        Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

    // ── P1-4：空态与列表可见性（Results 变化时由 UpdateResultVisibility 同步维护）──
    [ObservableProperty] private bool _hasNoResults = true;

    [ObservableProperty] private Visibility _resultsVisibility = Visibility.Collapsed;

    [ObservableProperty] private Visibility _noResultsVisibility = Visibility.Visible;

    /// <summary>
    /// 进度区常驻状态行的可见性（D-2.6）：运行期 InfoBar 关闭、由该行承载实时状态；
    /// 终态由 InfoBar 横幅承载同一文案，此处收起以避免同一句话重复展示两遍。
    /// </summary>
    public Visibility StatusLineVisibility => StatusBarOpen ? Visibility.Collapsed : Visibility.Visible;

    partial void OnStatusBarOpenChanged(bool value)
    {
        // 打开终态横幅时把当前状态文本快照给它；所有终态路径都是「先设 StatusText 再置 StatusBarOpen=true」，
        // 因此此处取到的即终态文案。
        if (value) StatusBarMessage = StatusText;
        OnPropertyChanged(nameof(StatusLineVisibility)); // D-2.6：终态收起进度区状态行，避免与 InfoBar 同句重复
    }

    private void UpdateResultVisibility()
    {
        bool has = Results.Count > 0;
        HasNoResults = !has;
        ResultsVisibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoResultsVisibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    [RelayCommand]
    private async Task StartOrganizeAsync()
    {
        if (IsBusy) return;
        StatusBarOpen = false; // P1-1：新任务入口即关闭上一轮 InfoBar（覆盖校验失败/取消等所有提前返回路径）

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

        // P0-3：输出目录不得等于源目录、也不得位于源目录树内。否则递归扫描会把上一轮
        // 生成的产物再次当作输入（其路径不在续传索引中），无 AI 回退命名场景下副本逐轮线性累积。
        if (OperationModeIndex != (int)OperationMode.Rename && IsInsideTree(OutputFolder, SourceFolder))
        {
            StatusText = "已取消：输出文件夹不能与源文件夹相同或位于源文件夹内部——" +
                         "重复运行时上一轮生成的文件会被再次当作输入，造成重复副本逐轮累积。" +
                         "请把输出文件夹放到源文件夹之外。";
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; // P1-1：校验失败终态
            StatusBarOpen = true;
            AppendLog("已取消：输出文件夹位于源文件夹内部，未执行任何操作。");
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
                    StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; // P1-1：取消终态
                    StatusBarOpen = true;
                    AppendLog("已取消：未选择备份文件夹，未执行重命名。");
                    return;
                }
                BackupFolder = picked!;
            }

            // P0-3：备份文件夹同样不得位于源目录树内——备份副本会被下一轮运行重新当作输入
            if (IsInsideTree(BackupFolder, SourceFolder))
            {
                StatusText = "已取消：备份文件夹不能位于源文件夹内部——备份出的副本会在下次运行时被再次当作输入。请另选源文件夹之外的备份文件夹。";
                StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; // P1-1：校验失败终态
                StatusBarOpen = true;
                AppendLog("已取消：备份文件夹位于源文件夹内部，未执行重命名。");
                return;
            }
        }

        IsBusy = true;
        Progress = 0;
        Results.Clear();
        LogText = "";
        RateText = ""; // 新批次：清掉上一轮的速率观测，避免旧数字被当成当前批次的实况
        StatusBarOpen = false; // P1-1：新任务开始，关闭上一轮 InfoBar
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var req = BuildRequest();
            var progress = new Progress<OrganizeProgress>(OnProgress);
            await AppServices.OrganizeService.RunAsync(req, progress, cts.Token);
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success; // P1-1：完成终态
            StatusBarOpen = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; // P1-1：取消终态
            StatusBarOpen = true;
            AppendLog("已取消。");
        }
        catch (System.Exception ex)
        {
            StatusText = "出错：" + ex.Message;
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error; // P1-1：失败终态
            StatusBarOpen = true;
            AppendLog("出错：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            IsPaused = false;
            RateText = ""; // 终态（完成/取消/出错）收起观测行：留着会成为无人更新的陈旧数字
            PauseButtonText = "暂停";
            BackupFolder = ""; // 下次执行重新弹窗让用户确认备份位置

            // 配置属于用户输入（源/输出文件夹、命名模板、引擎与模式选择），与本次运行成功与否无关：
            // 取消 / 失败 / 环境错误中止时同样应落盘，否则用户刚填好的路径与模板会随一次失败一起丢失。
            // 包 try/catch：持久化失败不得掩盖主流程的异常或终态。
            try
            {
                await PersistConfigAsync();
            }
            catch (System.Exception pex)
            {
                AppendLog("配置保存失败：" + pex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task ArchiveByDateAsync()
    {
        if (IsBusy) return;
        StatusBarOpen = false; // P1-1：新任务入口即关闭上一轮 InfoBar（覆盖校验失败/取消等所有提前返回路径）

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

        // P0-3：归档同理——输出（yyyy/yyyy-MM-dd 子目录）在源目录树内会把归档产物再次当输入累积
        if (IsInsideTree(OutputFolder, SourceFolder))
        {
            StatusText = "已取消：输出文件夹不能与源文件夹相同或位于源文件夹内部——" +
                         "归档产物会被下次运行再次当作输入，造成重复副本逐轮累积。" +
                         "请把输出文件夹放到源文件夹之外。";
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; // P1-1：校验失败终态
            StatusBarOpen = true;
            AppendLog("已取消：输出文件夹位于源文件夹内部，未执行任何操作。");
            return;
        }

        IsBusy = true;
        Progress = 0;
        Results.Clear();
        LogText = "";
        RateText = ""; // 新批次：清掉上一轮的速率观测，避免旧数字被当成当前批次的实况
        StatusBarOpen = false; // P1-1：新任务开始，关闭上一轮 InfoBar
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var req = BuildRequest();
            var progress = new Progress<OrganizeProgress>(OnProgress);
            await AppServices.OrganizeService.ArchiveByDateAsync(req, progress, cts.Token);
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success; // P1-1：完成终态
            StatusBarOpen = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。";
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning; // P1-1：取消终态
            StatusBarOpen = true;
            AppendLog("已取消。");
        }
        catch (System.Exception ex)
        {
            StatusText = "出错：" + ex.Message;
            StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error; // P1-1：失败终态
            StatusBarOpen = true;
            AppendLog("出错：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            IsPaused = false;
            RateText = ""; // 终态（完成/取消/出错）收起观测行：留着会成为无人更新的陈旧数字
            PauseButtonText = "暂停";
            BackupFolder = ""; // 下次执行重新弹窗让用户确认备份位置

            // 配置属于用户输入（源/输出文件夹、命名模板、引擎与模式选择），与本次运行成功与否无关：
            // 取消 / 失败 / 环境错误中止时同样应落盘，否则用户刚填好的路径与模板会随一次失败一起丢失。
            // 包 try/catch：持久化失败不得掩盖主流程的异常或终态。
            try
            {
                await PersistConfigAsync();
            }
            catch (System.Exception pex)
            {
                AppendLog("配置保存失败：" + pex.Message);
            }
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
            // 服务在「扫描文件 / 加载索引」阶段可能尚未创建暂停令牌，此时 Pause() 是 no-op。
            // 必须以服务的真实状态为准来翻转 UI，否则会出现「UI 显示已暂停、整批却继续跑完」的谎报。
            if (AppServices.OrganizeService.IsPaused)
            {
                IsPaused = true;
                PauseButtonText = "继续";
                // 文案必须说清生效时机：AI 调用内部（退避等待 / HTTP 往返）不可中断，
                // 暂停在「当前步骤完成后」才挂起（ProcessOneAsync 的 AI 前后各有检查点）。
                // 说「已暂停」会让人以为立刻停了 —— 那就是 P33 谎报。
                StatusText = "已暂停，点击「继续」恢复处理（当前步骤完成后生效）。";
                AppendLog("已暂停（当前步骤完成后生效）。");
            }
            else
            {
                // 兜底：暂停确实没生效。明确告知并引导改用「取消」，避免用户以为已暂停而离开。
                // （设置 StatusText 后再打开 StatusBarOpen：OnStatusBarOpenChanged 会快照文案到横幅）
                StatusText = "当前阶段（扫描文件 / 加载索引）暂不支持暂停，请稍候或点击「取消」。";
                StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
                StatusBarOpen = true;
            }
        }
    }

    /// <summary>
    /// 导航进入整理页时，把引擎选择同步为磁盘最新值（由 <c>OrganizePage.OnNavigatedTo</c> 调用）。
    /// 背景：本 VM 是 <see cref="AppServices.OrganizeVm"/> 单例，<c>AiProviderIndex</c> 只在构造时
    /// 初始化一次；用户在「设置」页改引擎并保存后，整理页（含它自己的引擎下拉框）仍是会话开始时的旧值
    /// → 设置页显示 A、实际生效 B（P33 谎报），连带新填的 RPM 上限也被整体忽略。
    /// <b>不做「无条件以磁盘覆盖」</b>：整理页自己也有引擎下拉框（OrganizePage.xaml 的 ComboBox），
    /// 用户可在当次手动改选；若当前值已偏离上次同步值即判定为手动改过，同步跳过，
    /// 避免「整理页选了 A → 切到别页再回来被改回 B」。
    /// </summary>
    public void SyncProviderFromDisk()
    {
        // A-11：配置读取失败在整理页也要可见 —— 用户可能从不进「设置」页，
        // 只在下次启动时发现「我填的路径 / 模板 / 引擎全没了」，且原文件已被覆盖、无从察觉。
        ShowLoadFailureWarningIfNeeded();

        int disk = (int)_settings.Load().AiProvider;
        if (disk == AiProviderIndex)
        {
            _syncedProviderIndex = disk; // 已一致（含「手动值刚被持久化」的情况）：重新对齐基线
            return;
        }
        if (AiProviderIndex != _syncedProviderIndex) return; // 用户手动改过 → 保留其当次选择
        AiProviderIndex = disk;
        _syncedProviderIndex = disk;
    }

    /// <summary>
    /// A-11：settings.json 反序列化 / 读取失败时在整理页也明确告知用户。
    /// 此时磁盘那份配置读不出来（已被备份到同目录），而内存里是整份归零的默认值——
    /// 不提示的话用户会以为是自己没填过，还可能在不知情的情况下让整理结束的自动持久化把它写回去。
    /// </summary>
    /// <remarks>标志只挂在 <see cref="SettingsService"/> 实现上（不进 ISettingsService 契约），故按具体类型读取。</remarks>
    private void ShowLoadFailureWarningIfNeeded()
    {
        if (_settings is not SettingsService settings) return;
        if (!settings.LoadFailedFromDisk) return;

        var backup = settings.LastCorruptedBackupPath;
        var where = string.IsNullOrEmpty(backup) ? "" : "（已备份到 " + backup + "）";
        StatusText = "配置文件读取失败" + where +
                     "，为避免覆盖，本次未自动保存；请在「设置」页检查文件或重新配置。";
        StatusSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        StatusBarOpen = true;
        AppendLog("配置文件读取失败" + where +
                  "，为避免覆盖原文件，整理结果对应的配置本次未自动写入磁盘。");
    }

    private OrganizeRequest BuildRequest()
    {
        _model = _settings.Load(); // 单例 VM 可能滞后于「设置」页改动，每次构建请求时刷新密钥/端点
        var provider = (AiProvider)AiProviderIndex;
        string key = provider switch
        {
            AiProvider.Zhipu => _model.ZhipuApiKey,
            AiProvider.Qwen => _model.QwenApiKey,
            AiProvider.Nvidia => _model.NvidiaApiKey,
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
            CustomApiRpmLimit = _model.CustomApiRpmLimit,
            Language = _model.Language,
            UseExifDate = UseExifDate,
            BackupFolder = BackupFolder,
        };
    }

    private async Task PersistConfigAsync()
    {
        // A-11：配置损坏保护 —— 整理结束的持久化属于「自动保存」，此时 _model 来自失败的 Load
        // （整份归零的默认值，含 4 个密钥），写下去就把用户还没确认的损坏文件顶掉了。
        // 原文件在 Load 失败那一刻已备份，但在用户于「设置」页确认之前一律不自动写盘。
        if (_settings is SettingsService settingsImpl && settingsImpl.HasUnacknowledgedLoadFailure)
        {
            var backup = settingsImpl.LastCorruptedBackupPath;
            AppendLog("配置未保存：配置文件读取失败" +
                      (string.IsNullOrEmpty(backup) ? "" : "（已备份到 " + backup + "）") +
                      "，为避免覆盖原文件本次未自动写入；请在「设置」页确认后保存。");
            return;
        }

        // P1-A 修复：_model 是 BuildRequest 时（任务开始）加载的快照，长任务期间设置页
        // 可能刚保存过密钥等配置。保存前重读磁盘，只覆盖整理页拥有的字段，避免覆盖其它来源的改动。
        _model = _settings.Load();
        _model.DefaultFolder = SourceFolder;
        _model.OutputFolder = OutputFolder;
        _model.NamingTemplate = NamingTemplate;
        _model.DryRun = DryRun;
        _model.UseExifDate = UseExifDate;
        _model.AiProvider = (AiProvider)AiProviderIndex;
        _model.OperationMode = (OperationMode)OperationModeIndex;
        _model.ConflictStrategy = (ConflictStrategy)ConflictIndex;
        await _settings.SaveAsync(_model);
        // 当次选择已落盘 → 与磁盘重新对齐，之后的「设置页改引擎」才应同步进整理页
        _syncedProviderIndex = AiProviderIndex;
    }

    private void OnProgress(OrganizeProgress p)
    {
        if (!string.IsNullOrEmpty(p.Message)) StatusText = p.Message;
        if (!string.IsNullOrEmpty(p.LogLine))
        {
            LogText += p.LogLine + "\n";
            if (LogText.Length > 20000) LogText = LogText.Substring(LogText.Length - 20000);
        }

        // A-11 防御：只在服务给出有效正值时推进进度，且不允许倒退。
        // 静态审查确认当前 18 处 progress.Report 里 4 处不带 Percent 的路径不可达，所以现在不是 bug；
        // 但服务侧将来只要新增一个漏带 Percent 的 Report，无条件赋值就会把进度条打回 0（用户以为卡死）。
        if (p.Percent > 0) Progress = System.Math.Max(Progress, p.Percent);

        // 速率观测行：只在服务给出有效样本时刷新（null = 未启用 AI / 真实请求不足 2 次 / 全命中缓存），
        // 保留上一次的值而不是清空——本批次仍在跑，旧值仍是「最近一分钟」的真实读数。
        if (p.AiRpm.HasValue) RateText = FormatRateLine(p.AiRpm.Value, p.AiLatencyMs, p.AiGateRpm);

        // 限制界面集合增长：超出上限时滚动丢弃最旧一条，始终保留最近 MaxResults 条；
        // 完整结果仍在服务端的 report.Results 与输出目录 rename_log.csv 中。
        if (p.Result != null)
        {
            if (Results.Count >= MaxResults) Results.RemoveAt(0);
            Results.Add(p.Result);
        }
    }

    /// <summary>
    /// 拼出速率观测行：<c>AI 实际 4.0 张/分 · 服务端响应 15.2 秒/张 · 闸门上限 30 张/分（未触发，瓶颈在服务端）</c>。
    /// 三个数必须同屏：单看「闸门 30」会以为是速率目标，单看「实际 4」又会以为是我们限速限错了；
    /// 并排之后才能看出 4 &lt;&lt; 30、闸门一次都没满过，慢的是服务端（单张就要 15 秒）。
    /// </summary>
    /// <param name="rpm">最近 60 秒窗口内完成的真实请求数。</param>
    /// <param name="latencyMs">最近一次真实请求的端到端时长（毫秒）；null 时省略该段。</param>
    /// <param name="gateRpm">闸门上限；null 表示本引擎不带闸门。</param>
    private static string FormatRateLine(double rpm, int? latencyMs, int? gateRpm)
    {
        var sb = new StringBuilder();
        sb.Append("AI 实际 ").Append(rpm.ToString("F1", CultureInfo.InvariantCulture)).Append(" 张/分");
        if (latencyMs.HasValue)
            sb.Append(" · 服务端响应 ").Append((latencyMs.Value / 1000.0).ToString("F1", CultureInfo.InvariantCulture)).Append(" 秒/张");

        sb.Append(" · 闸门");
        if (gateRpm.HasValue)
        {
            sb.Append("上限 ").Append(gateRpm.Value.ToString(CultureInfo.InvariantCulture)).Append(" 张/分");
            // 实际速率不足上限一半 → 闸门连窗口都没填满过，不可能在等闸门（P33：别把瓶颈赖给限速）
            if (rpm < gateRpm.Value * 0.5) sb.Append("（未触发，瓶颈在服务端）");
        }
        else
        {
            // 不带闸门的引擎（智谱/通义/通用档）：明确写「不限」，留空会被误读成「0 张/分」
            sb.Append(" 不限");
        }
        return sb.ToString();
    }

    private const int MaxResults = 2000;

    private void AppendLog(string line) => LogText += line + "\n";

    /// <summary>
    /// 判断 candidate 是否等于 baseDir 或位于 baseDir 目录树内部（P0-3 校验的判定核心）。
    /// 归一化全路径后比较，兼容大小写差异、尾部斜杠与根目录（如 D:\）写法；
    /// 路径非法时按"在树内"处理（宁误拦不放过，避免数据放大）。
    /// </summary>
    private static bool IsInsideTree(string candidate, string baseDir)
    {
        try
        {
            string c = Path.GetFullPath(candidate).TrimEnd('\\', '/');
            string b = Path.GetFullPath(baseDir).TrimEnd('\\', '/');

            if (string.Equals(c, b, StringComparison.OrdinalIgnoreCase)) return true;
            return c.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
