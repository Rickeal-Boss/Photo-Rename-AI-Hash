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

    /// <summary>
    /// ⑤（第九轮 F3）：整理页是否已就「本机解不开 settings.json 里的 API Key」提示过一次。
    /// 同 <see cref="_loadFailureNotified"/> 的理由：每次导航进整理页都会重新读盘，
    /// 不加标志会反复重弹；密钥重新填好保存后（标志复位）将来再发生仍会提示一次。
    /// </summary>
    private bool _undecryptableKeysNotified;

    // ── ②（第九轮 R9-4）：本批次失败清单 ──
    /// <summary>
    /// 本批次所有失败条目（<b>不设上限</b>）。与界面集合 <see cref="Results"/> 分开维护的原因：
    /// <see cref="Results"/> 有 <see cref="MaxResults"/> 上限，超出会 <c>RemoveAt(0)</c> 顶掉最早一条，
    /// 于是「5000 张里失败 300 张」的场景下早期失败会被逐条挤出屏幕，用户拿不到完整清单。
    /// 这里另存一份，批次结束时整体落盘（见 <see cref="WriteFailureListAsync"/>）。
    /// 只在 UI 线程访问（Progress&lt;T&gt; 回调与批次 finally 都在 UI 线程），无需加锁。
    /// </summary>
    private readonly List<RenameLogEntry> _batchFailures = new();

    // ── ④（第九轮 R9-6）：本批次「覆盖被降级为自动改名」的计数 ──
    /// <summary>重命名模式下「冲突策略=覆盖」被降级为自动加序号的文件数（内核不允许原地覆盖）。</summary>
    private int _degradedRenameMode;

    /// <summary>「覆盖」撞上本批次已产出的目标、被降级为自动加序号的文件数。</summary>
    private int _degradedBatchCollision;

    /// <summary>
    /// 有降级说明、但原因文案不在上面两类已知模式里的条目数（<b>中性兜底桶</b>）。
    /// 存在的意义：本 VM 靠<b>匹配内核文案</b>来区分降级原因（内核只把原因写进 <c>Message</c>，没有结构化标志）。
    /// 若内核日后改了文案措辞，条目会落进这里 → 汇总时只说「未按覆盖处理、已自动改名」，
    /// <b>不硬套一个具体原因</b>；否则会把 A 原因谎报成 B 原因（P33）。
    /// 逐条的真实原因始终在结果列表各行的提示里。
    /// </summary>
    private int _degradedOther;

    // ── ⑩（第十轮 R10）：本批次「真的覆盖了目标、且没有备份」的计数 ──
    /// <summary>
    /// 归档 / 整理（Move、Copy）路径在「冲突策略=覆盖」且目标位置原本已有同名且<b>内容不同</b>的文件时，
    /// 目标文件已被<b>无备份地</b>替换的条目数。备份只发生在「重命名模式」的<b>源</b>文件上
    /// （<c>BackupOriginalAsync</c>），归档 / 整理这两条路径不备份<b>目标</b>。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须与上面三个桶分开</b>：它们是<b>相反</b>的后果——降级桶是「<b>没有</b>覆盖、改了名、原内容还在」，
    /// 本桶是「<b>真的</b>覆盖了、没改名、原内容不可恢复」。并进中性桶（<see cref="_degradedOther"/>）就会
    /// 把「已覆盖」汇总成「已自动加序号改名」，即把「内容被替换」谎报成「内容原样保留」（P33）。
    /// <b>为什么不用 <c>_degraded*</c> 命名</b>：这不是「覆盖被降级」，而是覆盖<b>已经生效</b>的后果告知，
    /// 名字带 degraded 会误导后续维护者把它当成第四个降级原因。
    /// </remarks>
    private int _overwroteNoBackup;

    /// <summary>由页面注入：重命名实际执行前弹出备份文件夹选择。返回 null 表示用户取消。</summary>
    public Func<Task<string?>>? BackupFolderPicker { get; set; }

    public OrganizeViewModel()
    {
        _model = _settings.Load();
        SourceFolder = _model.DefaultFolder;
        OutputFolder = _model.OutputFolder;
        BackupFolder = _model.BackupFolder; // ③：与源/输出文件夹同等待遇，恢复上次选定的备份位置
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

    /// <summary>新批次开始时清空本批次的统计（失败清单与降级计数），避免上一批的数字串到这一批。</summary>
    private void ResetBatchTracking()
    {
        _batchFailures.Clear();
        _degradedRenameMode = 0;
        _degradedBatchCollision = 0;
        _degradedOther = 0;
        _overwroteNoBackup = 0;
    }

    /// <summary>
    /// 记录单个结果条目里「用户需要知道、但界面列表未必留得住」的信息：
    /// <list type="bullet">
    /// <item>失败条目 → 进 <see cref="_batchFailures"/>（不设上限，供批次结束时落盘完整清单）。</item>
    /// <item>「覆盖被降级为自动改名」的成功条目 → 计数（供终态横幅汇总，不再只藏在行 ToolTip 里）。</item>
    /// <item>「已覆盖且没有备份」的成功条目 → 单独计数（<see cref="_overwroteNoBackup"/>），与降级改名<b>分开</b>汇总。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 判定依据：内核只在三种情况下给<b>成功</b>条目写 <c>Message</c>
    /// （<c>OrganizeService</c> 的 <c>entry.Message = degradeReason</c> 两处，均在 <c>degraded</c> 为真时，
    /// 外加归档 / 整理两侧 <c>entry.Message = OverwriteNoBackupNotice</c>）；
    /// 其余成功条目的 <c>Message</c> 恒为空，错误条目则已被上面的 <see cref="RenameLogEntry.IsError"/> 分流。
    /// 降级那两类按文案区分：重命名模式的说明含「重命名模式」，本批次占用冲突的含「本批次」；
    /// 两者都不含时落 <see cref="_degradedOther"/>（中性兜底，汇总时不硬套原因）。
    /// <b>第三种成功条目文案</b>（第十轮 R10）：<c>OrganizeService.OverwriteNoBackupNotice</c>，
    /// 含「已覆盖」，表示覆盖<b>已经生效</b>、目标文件已被无备份替换——与上面两类是相反的后果，单独成桶。
    /// </remarks>
    private void TrackBatchIssues(RenameLogEntry entry)
    {
        if (entry.IsError)
        {
            _batchFailures.Add(entry);
            return;
        }
        if (!entry.IsSuccess || string.IsNullOrWhiteSpace(entry.Message)) return;

        // 归类依据是内核的<b>文案</b>（内核没有结构化标志）。三类已知文案见 OrganizeService：
        // 「重命名模式下不会覆盖源文件夹中的其它文件…」「目标已被本批次其他文件占用…」
        // 与 OverwriteNoBackupNotice「已覆盖输出目录中已存在的同名文件（该文件没有备份）」。
        //
        // 【匹配顺序即优先级，不要调整】「已覆盖」必须排在最前：
        // ① 语义上它与另外两类互斥且相反（覆盖已生效 vs 覆盖被降级），一旦被别的规则先吃掉，
        //    汇总就会把「内容被替换、不可恢复」说成「只改了名、内容还在」——P33 谎报；
        // ② 现有文案恰好互不包含（降级文案写的是「不会覆盖…」，无「已覆盖」子串），
        //    但内核措辞将来若把「已覆盖」并入更长的句子，前置判定是唯一能兜住改动方向的顺序；
        // ③ 万一将来某条文案同时含两类关键字，前置判定会把它记成「已覆盖」——方向上偏「多警告」：
        //    把真覆盖说成改名 = 用户以为内容还在（静默数据损失）；把改名说成覆盖 = 虚惊一场。
        //    两者代价不对称，故一律取更保守的那一侧。
        // 都不匹配时落中性桶，汇总时不硬套原因——宁可少说，也不能把原因说错（P33）。
        if (entry.Message.Contains("已覆盖", StringComparison.Ordinal)) _overwroteNoBackup++;
        else if (entry.Message.Contains("重命名模式", StringComparison.Ordinal)) _degradedRenameMode++;
        else if (entry.Message.Contains("本批次", StringComparison.Ordinal)) _degradedBatchCollision++;
        else _degradedOther++;
    }

    /// <summary>
    /// ④（第九轮 R9-6）：把「覆盖被静默降级为自动改名」在本批次结束时<b>汇总说一次</b>。
    /// 此前只有结果行的 ToolTip 里有原因，用户不逐行悬停就完全看不到——选「覆盖」却得到 <c>_1</c> 后缀，
    /// 会以为策略没生效或程序有问题。
    /// </summary>
    /// <remarks>
    /// ⑩（第十轮 R10）本方法同时汇总<b>相反</b>的另一类后果：覆盖<b>已经生效</b>且目标文件无备份
    /// （见 <see cref="_overwroteNoBackup"/>）。两者都必须说，且必须<b>分开</b>说——一个是「改了名、内容原样」，
    /// 一个是「没改名、内容已被替换且不可恢复」，合成一句就是把后者谎报成前者（P33）。
    /// </remarks>
    private void ReportDegradedSummary()
    {
        if (_degradedRenameMode == 0 && _degradedBatchCollision == 0 && _degradedOther == 0 &&
            _overwroteNoBackup == 0) return;

        var parts = new List<string>();
        // 「已覆盖」排在首位：它是本批次唯一「已经造成不可恢复后果」的一类，最需要用户先看到。
        if (_overwroteNoBackup > 0)
            parts.Add($"{_overwroteNoBackup} 个目标位置的原有文件已被「覆盖」策略直接替换，" +
                      "而这些被替换的文件没有备份、原内容不可恢复");
        if (_degradedRenameMode > 0)
            parts.Add($"{_degradedRenameMode} 个文件因「重命名模式不支持覆盖」而自动加序号改名（未覆盖源文件夹中的其它文件）");
        if (_degradedBatchCollision > 0)
            parts.Add($"{_degradedBatchCollision} 个文件因目标已被本批次其它文件占用而自动加序号改名");
        if (_degradedOther > 0)
            parts.Add($"{_degradedOther} 个文件未按「覆盖」处理、已自动加序号改名（原因见结果列表各行提示）");

        // 模拟运行下没有任何文件被改动，措辞必须是「预估」而不是既成事实（P33：不能把预览说成已发生）
        var note = (DryRun ? "模拟运行预估：本批次将有 " : "本批次有 ") + string.Join("；", parts) +
                   "。逐条原因见结果列表中各行的提示（悬停状态列）。";
        if (_overwroteNoBackup > 0)
        {
            // 补一句「两者相反」的点题：否则用户看到同一段汇总里既有「已覆盖」又有「已改名」，
            // 很容易读成「同一批文件既被覆盖又被改名」，反而更糊涂。
            note += "注意：「已覆盖」与「已自动加序号改名」是相反的结果——前者目标文件的内容已被替换且不可恢复，" +
                    "后者只是文件名多了一个序号、文件内容原样保留。";
        }
        AppendLog(note);
        // 用 Warning 而非成功色：这不是失败，但用户选择「覆盖」却没被覆盖、或已不可恢复地覆盖了别的文件，都必须显眼
        AppendTerminalNote(note, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
    }

    /// <summary>
    /// ②（第九轮 R9-4）：把本批次的失败清单落盘，供用户跑完后逐条排查。
    /// </summary>
    /// <remarks>
    /// <b>为什么选「落盘」而不是「界面导出按钮」：</b>
    /// <list type="number">
    /// <item>导出要求用户跑完时还坐在电脑前并主动点按钮；几千张的批次常在无人值守时结束，
    /// 或用户直接关掉应用，导出能力在那时就等于没有。落盘是「跑完即已留存」。</item>
    /// <item>界面结果列表有 <see cref="MaxResults"/> 上限，早期失败会被顶掉——只有落盘才能给全量。</item>
    /// <item>导出还得再走一次文件夹选择器与页面 code-behind，而落盘可完全在 VM 内完成。</item>
    /// </list>
    /// <b>为什么落在 <c>%USERPROFILE%\.PhotoRenameAIHash\</c> 而不是输出目录（与 rename_log.csv 同处）：</b>
    /// <list type="number">
    /// <item>失败原因里最常见的就是「输出盘写满 / 输出目录不可写」——把失败清单写进那个盘等于同样写不进去。</item>
    /// <item>重命名模式下输出目录 == 源目录，往用户照片目录里丢一个 CSV 属于污染。</item>
    /// <item>该目录是本项目 settings.json 的既定位置（见 <c>SettingsService.FilePath</c>），
    /// 也是项目自己判定「卸载 / 重置不会被系统清掉」的位置——沿用既有约定，不新造存储位置。</item>
    /// </list>
    /// <b>文件名带时间戳、且只在有失败时写：</b>不带时间戳会与上一批的清单互相覆盖（用户可能正想留着昨天的 300 条）；
    /// 只在有失败时写，则不会像 <c>rename_log.csv</c> 那样每批都增长（失败批次本身是少数）。
    /// <b>全方法不抛</b>：写清单是附带能力，绝不能因为它失败而影响批次终态或配置持久化。
    /// </remarks>
    private async Task WriteFailureListAsync()
    {
        if (_batchFailures.Count == 0) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".PhotoRenameAIHash");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "organize_failures_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");

            var sb = new StringBuilder();
            // 头部三行注释：让文件脱离界面也能自解释（用户几周后再打开，仍知道这是哪一批、什么参数）
            sb.AppendLine("# 本批次失败清单：失败 " + _batchFailures.Count.ToString(CultureInfo.InvariantCulture) + " 项" +
                          (DryRun ? "（模拟运行，未改动任何文件）" : ""));
            sb.AppendLine("# 批次时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                          "　模式：" + SelectedMode + "　" + (DryRun ? "模拟运行" : "实际执行"));
            sb.AppendLine("# 源文件夹：" + SourceFolder + "　输出文件夹：" + OutputFolder);
            sb.AppendLine("Timestamp,OriginalName,OriginalPath,Status,Message");
            foreach (var e in _batchFailures)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    Csv(e.OriginalName),
                    Csv(e.OriginalPath),
                    Csv(e.Status),
                    Csv(e.Message),
                }));
            }

            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true)).ConfigureAwait(true);

            var note = "失败清单已保存：" + path;
            AppendLog(note);
            AppendTerminalNote(note, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            // 落盘失败不影响批次结果；但要说出来，避免用户以为「没有清单 = 没有失败」
            AppendLog("失败清单写入失败：" + ex.Message + "（失败详情仍见上方结果列表与日志）。");
        }
    }

    /// <summary>CSV 字段转义（与 <c>RenameLogService.Csv</c> 同口径）：引号翻倍、CR/LF 折成空格。</summary>
    private static string Csv(string? s)
        => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";

    /// <summary>
    /// 往终态横幅追加一条补充说明（横幅此刻通常已经开着，因此必须显式写 <see cref="StatusBarMessage"/>，
    /// 不能只置 <c>StatusBarOpen = true</c> —— 同值赋值不触发钩子，追加的文案会在 UI 上不可见）。
    /// </summary>
    private void AppendTerminalNote(string note, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(note)) return;
        var baseMsg = string.IsNullOrWhiteSpace(StatusBarMessage) ? StatusText : StatusBarMessage;
        var msg = string.IsNullOrWhiteSpace(baseMsg) ? note : baseMsg + " " + note;
        ShowTerminalStatus(msg, severity);
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
            // ③：备份文件夹现在会被持久化沿用，因此「上次选的那个后来被删掉 / 换了盘符」是可达状态。
            // 内核的 BackupOriginalAsync 不创建备份目录（失败即环境级 → 整批中止），
            // 沿用失效路径会让用户在一张照片都没改的情况下被拦下；这里先探测，失效则退回弹窗重选。
            if (!string.IsNullOrWhiteSpace(BackupFolder) && !Directory.Exists(BackupFolder))
            {
                AppendLog("上次使用的备份文件夹已不存在：" + BackupFolder + "，请重新选择。");
                BackupFolder = "";
            }

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
        ResetBatchTracking(); // ②④：清空上一批的失败清单与降级计数
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
            // ③（第九轮 R9-5）：备份文件夹<b>不再清空</b>。此前每次跑完都置空，用户既看不到
            // 「备份到底在哪」，下次还得重选一遍；现在它与源/输出文件夹一样持久化沿用。

            // 配置属于用户输入（源 / 输出 / 备份文件夹、命名模板、引擎与模式选择），与本次运行成功与否无关：
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

            // ②④（第九轮 R9-4 / R9-6）：落盘完整失败清单 + 汇总「覆盖被降级为自动改名」。
            // 顺序固定为「先持久化配置、再追加这两条说明」：三者都可能往终态横幅追加文案，
            // 固定顺序可避免同一批次的横幅句子先后随路径而变（WriteFailureListAsync 内部自吞异常，不抛）。
            await WriteFailureListAsync();
            ReportDegradedSummary();
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
        ResetBatchTracking(); // ②④：清空上一批的失败清单与降级计数
        LogText = "";
        RateText = ""; // 新批次：清掉上一轮的速率观测，避免旧数字被当成当前批次的实况
        StatusBarOpen = false; // P1-1：新任务开始，关闭上一轮 InfoBar

        // 注意：此处<b>刻意不</b>追加 AiFieldHint。归档（ArchiveByDateAsync）不使用命名模板，
        // 也不调用识别接口——它把文件原样放进 yyyy/yyyy-MM-dd 子目录（命名取 f.Name）。
        // 把「模板里的 AI 字段会变成占位值 / 模板会被回退」写进归档日志，等于对本次运行做出
        // 一个不会发生的陈述（P33 谎报家族），故只保留在「开始整理」路径上。

        // ①（第九轮 R9-1）：归档语义必须留痕。此前模式=「重命名」时点归档，文件会被 File.Move 搬走
        // 且不备份、状态还写「已重命名」，而页面对「模式会影响归档」一个字都没说。
        // 文案逐条对齐内核实现（归档语义归一：Rename → Move；Copy 仍保留源文件），
        // 不写成「归档固定移动、不受模式影响」——那在「复制」模式下与事实不符（P33）。
        bool archiveKeepsSource = SelectedMode == OperationMode.Copy;
        AppendLog($"按日期归档：将按拍摄日期把照片{(archiveKeepsSource ? "复制" : "移动")}到 {OutputFolder} 下的 " +
                  $"yyyy/yyyy-MM-dd 子目录" +
                  (archiveKeepsSource ? "（源文件保留在原处）" : "（源文件会从源文件夹移走）") +
                  "；归档不做备份——即使模式选「重命名」也不会像「开始整理」那样先备份原件。");

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
            // ③（第九轮 R9-5）：备份文件夹<b>不再清空</b>。此前每次跑完都置空，用户既看不到
            // 「备份到底在哪」，下次还得重选一遍；现在它与源/输出文件夹一样持久化沿用。

            // 配置属于用户输入（源 / 输出 / 备份文件夹、命名模板、引擎与模式选择），与本次运行成功与否无关：
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

            // ②④（第九轮 R9-4 / R9-6）：落盘完整失败清单 + 汇总「覆盖被降级为自动改名」。
            // 顺序固定为「先持久化配置、再追加这两条说明」：三者都可能往终态横幅追加文案，
            // 固定顺序可避免同一批次的横幅句子先后随路径而变（WriteFailureListAsync 内部自吞异常，不抛）。
            await WriteFailureListAsync();
            ReportDegradedSummary();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>
    /// ③（第九轮 R9-5）：整理页上「浏览」备份文件夹。复用页面构造时注入的 <see cref="BackupFolderPicker"/>
    /// （与「重命名实跑前自动弹窗」同一个选择器），因此无需页面 code-behind 新增 Click 处理。
    /// 用户取消选择时保留原值，不清空——清空等于把用户已确认的备份位置丢掉。
    /// </summary>
    [RelayCommand]
    private async Task PickBackupFolderAsync()
    {
        if (BackupFolderPicker == null) return;
        var picked = await BackupFolderPicker();
        if (!string.IsNullOrWhiteSpace(picked)) BackupFolder = picked!;
    }

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
        // 顺带刷新本页的磁盘配置快照：密钥是否已配置会影响「模拟运行 AI 占位」提示的判定，
        // 用户在「设置」页填好 Key 后回到整理页，提示应当立刻消失。（_model 只在本方法、
        // 构造、BuildRequest、PersistConfigAsync 这几处被赋值，后三处都会先重新 Load，故无覆盖风险。）
        _model = _settings.Load();

        // 两条配置告警都必须放在上面那次 Load <b>之后</b>：这两个标志都由 Load 重算，
        // 在 Load 之前读到的是上一次的状态（可能已过期，例如用户刚在设置页把配置修好）。
        // 顺序与「设置」页保持一致：先「密钥解不开」，再「配置读取失败」——后者更严重，
        // 放后面是为了让它覆盖前者（两条同时成立时，用户看到的应是更严重的那条）。
        ShowUndecryptableKeysWarningIfNeeded();
        ShowLoadFailureWarningIfNeeded();

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

    /// <summary>
    /// ⑤（第九轮 F3）：settings.json 里的 API Key 是 <c>enc:</c> 密文、但本机凭据解不开
    /// （文件从其它账户 / 其它机器拷来，或凭据已损坏）时，在整理页也明确告知。
    /// </summary>
    /// <remarks>
    /// 此前只有设置页会提示，而整理页「每批结束都自动持久化」——用户可能从不进设置页，
    /// 于是「密钥解不开 → 被置空 → 整理结束的自动保存把磁盘上的密文覆盖成空串」这条
    /// <b>不可逆的密文销毁</b>全程无感（第九轮 F3 的后半段）。
    /// 本方法负责「让用户看见」；真正的「不覆盖」在 <see cref="PersistConfigAsync"/> 里。
    /// 提示只弹一次（同 <see cref="_loadFailureNotified"/> 的理由：每次导航都会调本方法）；
    /// 密钥重新填好并保存后标志复位，将来再发生仍会提示一次。
    /// </remarks>
    private void ShowUndecryptableKeysWarningIfNeeded()
    {
        if (_settings is not SettingsService settings) return;
        if (!settings.LastLoadHadUndecryptableKeys)
        {
            _undecryptableKeysNotified = false; // 已恢复正常 → 复位，下次再发生仍提示
            return;
        }
        if (_undecryptableKeysNotified) return;
        _undecryptableKeysNotified = true;

        ShowTerminalStatus("本机 Windows 凭据无法解密已保存的 API Key（设置文件可能来自其它账户或机器）。" +
                           "为避免把解不开的密文覆盖成空值，本页不再自动保存配置——" +
                           "本次在整理页改的路径 / 模板等改动同样不会落盘，关闭应用即丢失；" +
                           "请到「设置」页重新填写密钥并保存，之后本页的自动保存会恢复。",
                           Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        AppendLog("本机无法解密 settings.json 中的 API Key；整理页的自动保存已停用，以免覆盖掉磁盘上的密文。" +
                  "本页的路径 / 模板等改动因此也未保存，需在密钥恢复后重新保存。");
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

        // ⑤（第九轮 F3，后半段）：密钥「是密文但本机解不开」时，上面那次 Load 已把这些字段置空。
        // 若照常保存，写回的就是空串 → 磁盘上的 enc: 密文被覆盖掉，且不可逆（用户把文件拷回原机器也救不回来）。
        // 因此这里与「配置读取失败保护闩」同策：<b>本次不自动写入</b>，由用户到设置页重新填密钥后显式保存。
        // 代价是这一次整理页的路径 / 模板改动也不会自动落盘，但相比「不可逆地销毁密钥」这个代价可恢复。
        // 自愈：用户重填密钥并保存后，密文被换成可解密的新密文，下一次 Load 该标志即为 false，自动保存恢复。
        if (_settings is SettingsService keyImpl && keyImpl.LastLoadHadUndecryptableKeys)
        {
            // ⑩（第十轮 R10）：<b>必须把「本页改动也没落盘」说出来</b>。
            // 此前的文案只说「配置未自动保存…本次未写入」，用户很容易理解成「密钥没存上，路径模板存上了」，
            // 于是带着「已保存」的错觉继续用，直到重开应用发现路径 / 模板回到旧值——又是一次静默失败。
            // 提示里不能说「到设置页保存时会一并落盘」：设置页保存<b>只写它自己拥有的字段</b>
            // （见 SettingsViewModel 的 P1-A 注释，DefaultFolder / OutputFolder / BackupFolder / NamingTemplate
            // 都不在其回写清单内），本页这些改动只能靠本方法（批次结束时）落盘。
            var reason = "配置未自动保存：本机无法解密 settings.json 中的 API Key（文件可能来自其它账户或机器），" +
                         "为避免把解不开的密文覆盖成空值，本次未写入；" +
                         "本次在整理页所做的路径 / 模板等改动也一并未保存，关闭应用即丢失——" +
                         "请到「设置」页重新填写密钥并保存，再回到本页执行一次整理，跑完会自动保存本页改动。";
            AppendLog(reason);
            ShowTerminalStatus(reason, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            // 同闩分支：本次未落盘，但仍把当前值视为基线，避免留下永久性的「用户手动改过」误判
            _syncedProviderIndex = AiProviderIndex;
            _undecryptableKeysNotified = true; // 已经明确说过一次，避免同一次导航再弹一遍
            return;
        }

        _model.DefaultFolder = SourceFolder;
        _model.OutputFolder = OutputFolder;
        _model.BackupFolder = BackupFolder; // ③：备份文件夹与源/输出文件夹同等待遇持久化
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

        // ⑤（第九轮 F3，前半段）：SaveAsync 里 DPAPI 加密失败会<b>降级为明文落盘</b>且保存仍然成功。
        // 此前只有设置页的「保存设置」会读这个标志，而每批结束都跑的这条路径不读 →
        // 密钥明文静默落盘，与设置页「保存结果会明确提示密钥可能以明文存储」的承诺矛盾。
        if (_settings is SettingsService savedImpl && savedImpl.LastProtectFailed)
        {
            const string note = "注意：本机 DPAPI 加密不可用，本次保存的 API Key 可能以明文写入本机配置文件。";
            AppendLog(note);
            AppendTerminalNote(note, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        }
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

        // 限制界面集合增长：超出上限时滚动丢弃最旧一条，始终保留最近 MaxResults 条。
        // <b>但这只是界面层的取舍，不代表失败信息就此丢失</b>：失败条目同时进 _batchFailures
        // （不设上限）并在批次结束时落盘（见 WriteFailureListAsync）——
        // 此前失败清单既不在 rename_log.csv 里（内核只把成功/跳过写进 CSV）、又会被这里的上限顶掉，
        // 几千张里失败几百张时用户拿不到完整清单（第九轮 R9-4）。
        if (p.Result != null)
        {
            TrackBatchIssues(p.Result);
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
