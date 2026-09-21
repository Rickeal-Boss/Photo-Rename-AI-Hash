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

    /// <summary>
    /// A-11：整理页的「配置读取失败」横幅是否已就本次损坏提示过。
    /// 本 VM 是单例、<see cref="SyncProviderFromDisk"/> 每次导航进整理页都会被
    /// <c>OrganizePage.OnNavigatedTo</c> 调用一次，若不加标志，用户关掉横幅后再进整理页会反复重弹、
    /// 日志也被反复追加（同一次损坏提示 N 次）。配置恢复正常后复位，下次再损坏时仍会提示一次。
    /// </summary>
    private bool _loadFailureNotified;

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
        // 与下方 OperationMode / ConflictStrategy 同一口径：越界值落到失效安全档，而【不】钳到枚举上界。
        // AiProvider 的上界 Nvidia 会把一次「配置损坏」变成「调用付费引擎」（没配 Key 时还会把整批拦下）；
        // 落到 None（不启用识别）最坏只是不调 AI，且实跑会命中 AI 字段回退提示，用户可见。
        // 归一规则必须与 <see cref="SelectedProvider"/>、<see cref="SyncProviderFromDisk"/> 完全一致，
        // 否则同一份状态在两处含义不同（第八轮 P3-1）。
        AiProviderIndex = System.Enum.IsDefined(typeof(AiProvider), _model.AiProvider)
            ? (int)_model.AiProvider
            : (int)AiProvider.None;
        _syncedProviderIndex = AiProviderIndex; // 构造即视为已与磁盘对齐
        // P1-1：与 AiProvider 同一套路 —— System.Text.Json 数字→枚举不校验定义域，
        // settings.json 里手工编辑出的 "OperationMode": 7 会被原样读成越界枚举：
        // 既让 ComboBox 因无匹配项而空白，也会让「是否重命名模式」的判断失效
        // （越界值 != Rename →「重命名必须先选备份文件夹」的弹窗被绕过）。在读取处就钳进定义域。
        // 失效安全（fail-safe）：越界值【不】钳到枚举上界，而是落到破坏面最小的那一档。
        // 能走到这条分支的前提是「settings.json 被写坏 / 走了逐字段抢救路径」，本身就是异常起点。
        // 上界 Rename 是本应用破坏力最大的模式——上一轮 P0-2（重命名 + 冲突策略「覆盖」=
        // 静默删除源目录里的另一个文件，而备份只备份 source 不备份 target，不可恢复）正出在此模式；
        // 上界 Overwrite 是三种冲突策略里唯一会覆盖既有文件的。
        // 钳到上界 = 把配置损坏的用户送进破坏力最强的组合；落到 Copy + AutoRename 则最坏
        // 只是「多复制一份副本、原件不动」，不丢数据、随时可重来。
        OperationModeIndex = System.Enum.IsDefined(typeof(OperationMode), _model.OperationMode)
            ? (int)_model.OperationMode
            : (int)OperationMode.Copy;
        ConflictIndex = System.Enum.IsDefined(typeof(ConflictStrategy), _model.ConflictStrategy)
            ? (int)_model.ConflictStrategy
            : (int)ConflictStrategy.AutoRename;
        // P1-4：结果集合变化时同步空态/列表可见性
        Results.CollectionChanged += (_, __) => UpdateResultVisibility();
        UpdateResultVisibility();
        UpdateAiFieldHint(); // 构造期的三个钩子未必都触发（赋值顺序 / 值未变化），结尾补算一次
    }

    [ObservableProperty] private string _sourceFolder = "";

    [ObservableProperty] private string _outputFolder = "";

    [ObservableProperty] private int _aiProviderIndex;

    [ObservableProperty] private string _namingTemplate = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    [ObservableProperty] private int _operationModeIndex;

    [ObservableProperty] private int _conflictIndex;

    /// <summary>
    /// 钳制后的「操作模式」。凡是要把它交给内核、或拿它与枚举做比较的地方都必须走这里，
    /// 而不是直接用 <see cref="OperationModeIndex"/>：后者是 ComboBox 的双向绑定值，
    /// 控件在「无匹配项」时会回写 -1，而 <c>(OperationMode)(-1)</c> 既不是 Copy 也不是 Rename，
    /// 会让 <c>== OperationMode.Rename</c> 形式的判断静默失效（备份文件夹弹窗被绕过）。
    /// 只钳「赋值处」不够：后续的比较与写回仍会用到原值，故所有读取处统一走本属性。
    /// </summary>
    private OperationMode SelectedMode =>
        System.Enum.IsDefined(typeof(OperationMode), OperationModeIndex)
            ? (OperationMode)OperationModeIndex
            : OperationMode.Copy;

    /// <summary>
    /// 钳制后的「冲突处理策略」。同 <see cref="SelectedMode"/>：越界值一律落到破坏面最小的
    /// <see cref="ConflictStrategy.AutoRename"/>，<b>不</b>钳到枚举上界
    /// <see cref="ConflictStrategy.Overwrite"/>（三种冲突策略里唯一会覆盖既有文件的那一档）。
    /// </summary>
    private ConflictStrategy SelectedConflict =>
        System.Enum.IsDefined(typeof(ConflictStrategy), ConflictIndex)
            ? (ConflictStrategy)ConflictIndex
            : ConflictStrategy.AutoRename;

    /// <summary>
    /// 钳制后的「识别引擎」。同 <see cref="SelectedMode"/>：越界索引（ComboBox 无匹配项时回写的 -1、
    /// settings.json 残留的 99）会让 <c>(AiProvider)(-1)</c> 既不等于 <see cref="AiProvider.None"/>、
    /// 又落不进 <c>CreateAi</c> 的 switch，于是「引擎未启用」被静默当成「引擎已选但缺 Key」
    /// → 用户看到「已选择识别引擎「-1」但未配置 API Key」，把配置损坏谎报成缺密钥。
    /// 凡参与判断 / 交给内核 / 写盘的地方统一走本属性（ComboBox 双向绑定仍绑原始索引）。
    /// 越界值落到 <see cref="AiProvider.None"/>（不启用识别）而非枚举上界：不付费、不改名，
    /// 破坏面最小；若钳到上界 <see cref="AiProvider.Nvidia"/>，反而会在无密钥时把整批拦下。
    /// </summary>
    private AiProvider SelectedProvider =>
        System.Enum.IsDefined(typeof(AiProvider), AiProviderIndex)
            ? (AiProvider)AiProviderIndex
            : AiProvider.None;

    [ObservableProperty] private bool _dryRun;

    // ── AI 字段可用性提示（「开始整理」按钮上方的 Warning InfoBar + 日志框） ──
    // 模板里写了 AI 占位符、而本次运行拿不到真实识别值时，必须在<b>开始之前</b>说清会发生什么，
    // 否则用户只能在结果列表里发现「文件名不是我写的模板」（P33 谎报）。两种后果完全不同，故文案分两支：
    //   ① 模拟运行：内核完全不调用 AI，AI 占位符落成「unknown + 该文件在批次中的序号(4 位)」，
    //      形如 unknown0007（带序号是必要的：默认模板六个占位符全是 AI 字段、不含 {name}/{n}，
    //      若一律填同一个 unknown，同一批每个文件的候选名一字不差 → 预览全变 _1/_2/_3 楼梯）。
    //   ② 实际执行：内核会把整条模板换成默认模板 {yyyy}{MM}{dd}_{name}_{n}
    //      （见 ProcessOneAsync 的 aiValueUnavailable 回退），用户填的命名规则<b>完全不生效</b>——
    //      比模拟运行更彻底，此前却一个字都没提示（第八轮 P1-2 / 元模式 E）。

    /// <summary>命名模板里的 AI 占位符（与内核 <c>OrganizeService.AiPlaceholders</c> 同一口径，大小写不敏感）。</summary>
    private static readonly string[] AiPlaceholders =
    {
        "{category}", "{scene}", "{people}", "{action}", "{subtitle}", "{source}",
    };

    /// <summary>AI 字段可用性提示文案；空串表示当前组合不需要提示。</summary>
    [ObservableProperty] private string _aiFieldHint = "";

    /// <summary>
    /// 提示是否成立。
    /// </summary>
    /// <remarks>
    /// <b>判据有意比内核 <c>TemplateWillUseAi</c> 更宽，不是它的镜像。</b>
    /// 内核判据只回答一个问题——「本批次会不会真的调用识别接口」，等价于
    /// 「非模拟运行 &amp;&amp; 模板含 AI 占位符」，<b>既不看引擎选择、也不看密钥</b>
    /// （密钥校验发生在更下游的 <c>CreateAi</c> 里）。
    /// 本提示回答的是另一个问题——「模板里的 AI 字段能不能取到真实值、取不到会怎样」，
    /// 因此必须把引擎选择与密钥配置一并算进来，天然比内核判据多出
    /// <see cref="SelectedProvider"/> 与 <c>KeyConfiguredFor</c> 两个合取项。
    /// 若强行与内核判据逐字对齐，反而会在「引擎已选 + 密钥已配 + 实跑」这种一切正常的组合下也弹提示。
    /// 三个分支各自对应内核的一处真实行为：
    /// <list type="bullet">
    /// <item><description>模拟运行 &amp;&amp; 引擎≠无 &amp;&amp; 缺 Key：<c>CreateAi</c> 因 <c>TemplateWillUseAi</c> 为 false
    /// 返回 null ⇒ 不调 AI，AI 字段落 unknown 占位；额外提示「切实跑会因缺 Key 整批中止」。</description></item>
    /// <item><description>实跑 &amp;&amp; 引擎=无：<c>ProcessOneAsync</c> 的 <c>aiValueUnavailable</c> 成立
    /// ⇒ 整条模板被换成 <c>DefaultNamingTemplate</c>。</description></item>
    /// <item><description>实跑 &amp;&amp; 引擎≠无 &amp;&amp; 缺 Key：<c>CreateAi</c> 抛整批级永久错误
    /// ⇒ 在扫描之前即中止整批（不改动任何文件）。</description></item>
    /// </list>
    /// </remarks>
    [ObservableProperty] private bool _hasAiFieldHint;

    /// <summary>模板是否用到 AI 占位符（大小写不敏感）。</summary>
    private static bool TemplateUsesAiPlaceholders(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        foreach (var p in AiPlaceholders)
        {
            if (template!.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// 所选引擎的密钥是否已配置。读 <see cref="_model"/> 的缓存快照而不是每次重新 Load()：
    /// 本方法会在命名模板每次按键时被调用，逐键做一次读盘 + DPAPI 解密太重。
    /// 快照由构造 / <see cref="SyncProviderFromDisk"/> / <see cref="BuildRequest"/> 这几处真实 Load 刷新。
    /// </summary>
    private bool KeyConfiguredFor(AiProvider provider) => provider switch
    {
        AiProvider.Zhipu => !string.IsNullOrWhiteSpace(_model.ZhipuApiKey),
        AiProvider.Qwen => !string.IsNullOrWhiteSpace(_model.QwenApiKey),
        AiProvider.Nvidia => !string.IsNullOrWhiteSpace(_model.NvidiaApiKey),
        AiProvider.Custom => !string.IsNullOrWhiteSpace(_model.CustomApiKey),
        _ => false,
    };

    /// <summary>
    /// 重算「AI 字段可用性」提示。<b>非阻断</b>：这里只负责在用户点「开始整理」之前把后果说出来，
    /// 不拦任何操作（模拟运行照样跑得完；实跑的两支本就由内核在中止时抛出真实错误）。
    /// </summary>
    private void UpdateAiFieldHint()
    {
        var provider = SelectedProvider;
        bool usesAi = TemplateUsesAiPlaceholders(NamingTemplate);
        string hint = "";

        if (usesAi && DryRun && provider != AiProvider.None && !KeyConfiguredFor(provider))
        {
            // ① 模拟运行 + 缺 Key：不调 AI、AI 字段落占位值；且切到实跑会整批中止
            hint = "模拟运行不会调用识别接口：模板里的 AI 字段（category / scene / people / action / subtitle / source）"
                   + "将以 unknown0007 这类「unknown + 批次序号」占位值显示，不是真实识别结果；"
                   + "且当前引擎尚未配置 API Key，切到「实际执行」时会因缺少密钥整批中止。";
        }
        else if (usesAi && !DryRun && provider == AiProvider.None)
        {
            // ② 实跑 + 引擎=无：整条模板被内核回退成默认模板，用户填的命名规则完全不生效。
            // 这是与①不同的另一件事（不是「字段变占位值」，而是「模板整个被换掉」），文案必须分开。
            hint = "当前识别引擎为「无」，模板里的 AI 字段（category / scene / people / action / subtitle / source）"
                   + "本次无法产出真实值：为避免生成 unknown 占位名，命名规则将被回退为默认模板 "
                   + "{yyyy}{MM}{dd}_{name}_{n}（日期_原名_序号），你填写的模板不会生效。"
                   + "若要用自己的模板，请在「识别引擎」中选择一个引擎并配置 API Key，或从模板中去掉 AI 字段。";
        }
        else if (usesAi && !DryRun && !KeyConfiguredFor(provider))
        {
            // ③ 实跑 + 引擎≠无（②已排除 None）+ 缺 Key：CreateAi 在扫描之前抛整批级永久错误。
            // 与①②都不同：既不是占位值、也不是模板被换掉，而是「一个文件都不会动、整批直接中止」。
            hint = "当前识别引擎尚未配置 API Key：点击「开始整理」后会立即整批中止，不会改动任何文件。"
                   + "请先在「设置」页为该引擎填写密钥，或从命名规则中去掉 AI 字段。";
        }

        AiFieldHint = hint;
        HasAiFieldHint = hint.Length > 0;
    }

    // 三个输入任一变化都要重算：运行模式、命名模板、引擎选择
    partial void OnDryRunChanged(bool value)
    {
        UpdateAiFieldHint();
    }

    partial void OnNamingTemplateChanged(string value)
    {
        UpdateAiFieldHint();
    }

    partial void OnAiProviderIndexChanged(int value)
    {
        UpdateAiFieldHint();
    }

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

    /// <summary>
    /// 终态提示的统一出口：同时写常驻状态行（<see cref="StatusText"/>）与终态横幅（<see cref="StatusBarMessage"/>）。
    /// </summary>
    /// <remarks>
    /// <b>必须显式写 StatusBarMessage，不能只依赖 <see cref="OnStatusBarOpenChanged"/> 的快照。</b>
    /// <c>[ObservableProperty]</c> 生成的 setter 带相等性判断：若横幅此刻已经开着
    /// （例如上一轮「整理完成」的成功横幅还没关，或保护闩拦截发生在成功终态之后），
    /// 末尾那句 <c>StatusBarOpen = true</c> 就是 no-op → 钩子不触发 → 横幅继续显示「整理完成」，
    /// 而承载真正提示的状态行又被 <see cref="StatusLineVisibility"/> 折叠 → 提示在 UI 上完全不可见
    /// （P33 谎报，且正是本轮要清零的「配置静默丢失」：用户看到成功横幅，配置却没落盘）。
    /// </remarks>
    private void ShowTerminalStatus(string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
    {
        StatusText = message;
        StatusSeverity = severity;
        StatusBarMessage = message; // 显式刷新横幅文案：不依赖 StatusBarOpen 的翻转
        StatusBarOpen = true;       // 已开着时这一句是 no-op，但文案上面已经写进去了
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

        if (SelectedMode != OperationMode.Rename &&
            (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder)))
        {
            StatusText = "请选择有效输出文件夹（重命名模式可留空）。";
            return;
        }

        // P0-3：输出目录不得等于源目录、也不得位于源目录树内。否则递归扫描会把上一轮
        // 生成的产物再次当作输入（其路径不在续传索引中），无 AI 回退命名场景下副本逐轮线性累积。
        if (SelectedMode != OperationMode.Rename && IsInsideTree(OutputFolder, SourceFolder))
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
        if (SelectedMode == OperationMode.Rename && !DryRun)
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

        // AI 字段可用性提示（模拟运行占位值 / 实跑模板被回退 / 实跑缺 Key 会中止）写进日志再开跑。
        // 这里刻意<b>不开横幅</b>——横幅一开就会折叠下方的实时状态行，整轮看不到进度；
        // 日志框是常驻的，跑完回头看也还在，且不影响进度展示。
        if (AiFieldHint.Length > 0) AppendLog(AiFieldHint);

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

        // 注意：此处<b>刻意不</b>追加 AiFieldHint。归档（ArchiveByDateAsync）不使用命名模板，
        // 也不调用识别接口——它把文件原样放进 yyyy/yyyy-MM-dd 子目录（命名取 f.Name）。
        // 把「模板里的 AI 字段会变成占位值 / 模板会被回退」写进归档日志，等于对本次运行做出
        // 一个不会发生的陈述（P33 谎报家族），故只保留在「开始整理」路径上。

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
                // （走 ShowTerminalStatus：横幅可能已开着，只设 StatusText 再置 StatusBarOpen 不会刷新横幅文案）
                ShowTerminalStatus("当前阶段（扫描文件 / 加载索引）暂不支持暂停，请稍候或点击「取消」。",
                                   Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
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

        // 顺带刷新本页的磁盘配置快照：密钥是否已配置会影响「模拟运行 AI 占位」提示的判定，
        // 用户在「设置」页填好 Key 后回到整理页，提示应当立刻消失。（_model 只在本方法、
        // 构造、BuildRequest、PersistConfigAsync 这几处被赋值，后三处都会先重新 Load，故无覆盖风险。）
        _model = _settings.Load();

        // try/finally：下面两条 return 是正常路径，但模拟运行提示无论走哪条都要重算
        try
        {
            // P2-4：System.Text.Json 数字→枚举不校验定义域（settings.json 写 "AiProvider": 99 不报错）。
            // 越界值会让下方 ComboBox 无匹配项而空白，也会让 CreateAi 抛「已选择识别引擎「99」但未配置 API Key」。
            // 在读取处就归一：既覆盖下面的比较，也覆盖赋值给 AiProviderIndex 的那一支
            // （只归一赋值处不够——disk=99 与归一后的当前值不等，会被误判成「磁盘值有变」而把越界值再写进 VM）。
            // 归一规则与构造 / <see cref="SelectedProvider"/> 完全一致：越界值落到 AiProvider.None，
            // 不钳到枚举上界 Nvidia —— 同一份状态在全文件只有一种含义。
            int disk = System.Enum.IsDefined(typeof(AiProvider), _model.AiProvider)
                ? (int)_model.AiProvider
                : (int)AiProvider.None;
            if (disk == AiProviderIndex)
            {
                _syncedProviderIndex = disk; // 已一致（含「手动值刚被持久化」的情况）：重新对齐基线
                return;
            }
            if (AiProviderIndex != _syncedProviderIndex) return; // 用户手动改过 → 保留其当次选择
            AiProviderIndex = disk;
            _syncedProviderIndex = disk;
        }
        finally
        {
            UpdateAiFieldHint();
        }
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
        if (!settings.LoadFailedFromDisk)
        {
            // P2-1：配置已恢复正常（例如用户在「设置」页成功保存、或文件被修好）→ 复位提示标志，
            // 这样将来再损坏一次时仍会提示，而不是被上一次的「已提示」永久吞掉。
            _loadFailureNotified = false;
            return;
        }
        // P2-1：同一次损坏只提示一次。本方法每次导航进整理页都会被调用，用户关掉横幅后再进来
        // 不该重弹，日志也不该被反复追加（此前每导航一次就多一条一模一样的「配置文件读取失败」）。
        if (_loadFailureNotified) return;
        _loadFailureNotified = true;

        var backup = settings.LastCorruptedBackupPath;
        var where = string.IsNullOrEmpty(backup) ? "" : "（已备份到 " + backup + "）";
        // 走 ShowTerminalStatus：横幅可能还开着上一轮的终态文案（同值赋值不触发钩子 → 提示不可见）
        ShowTerminalStatus("配置文件读取失败" + where +
                           "，为避免覆盖，本次未自动保存；请在「设置」页检查文件或重新配置。",
                           Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        AppendLog("配置文件读取失败" + where +
                  "，为避免覆盖原文件，整理结果对应的配置本次未自动写入磁盘。");
    }

    private OrganizeRequest BuildRequest()
    {
        _model = _settings.Load(); // 单例 VM 可能滞后于「设置」页改动，每次构建请求时刷新密钥/端点
        var provider = SelectedProvider; // 走归一属性：越界索引不得进请求（否则「未选引擎」会被当成「缺 Key」）
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
            Mode = SelectedMode,
            NamingTemplate = NamingTemplate,
            Conflict = SelectedConflict,
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
            var where = string.IsNullOrEmpty(backup) ? "" : "（已备份到 " + backup + "）";
            var reason = "配置未保存：配置文件读取失败" + where +
                         "，为避免覆盖原文件本次未自动写入；请在「设置」页确认后保存。";
            AppendLog(reason);
            // P2-2 / P0-1：拦截生效时只写日志不够 —— 用户此刻就在整理页，而横幅多半还停在
            // 「整理完成」的成功文案上，很容易漏看「配置其实没保存」。
            // 上一版这里只做「先设 StatusText 再置 StatusBarOpen=true」，但此刻 StatusBarOpen 已是 true，
            // 同值赋值不触发 OnStatusBarOpenChanged → 横幅文案不刷新 → 提示在 UI 上完全不可见。
            // 改走 ShowTerminalStatus，显式写 StatusBarMessage，不再依赖横幅的翻转。
            ShowTerminalStatus(reason, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            // P2-2：基线对齐原先写在 return 之后，被闩拦截时会被整段跳过 —— 下一次导航进整理页时
            // SyncProviderFromDisk 会把「当前值 != 陈旧基线」误判成「用户手动改过」而拒绝同步磁盘值。
            // 挪到 return 之前：本次未落盘，但仍把当前值视为基线，避免留下永久性误判。
            _syncedProviderIndex = AiProviderIndex;
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
        // 写回同样走归一属性：否则越界索引会被固化进 settings.json，成为持久非法值
        _model.AiProvider = SelectedProvider;
        _model.OperationMode = SelectedMode;
        _model.ConflictStrategy = SelectedConflict;
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
