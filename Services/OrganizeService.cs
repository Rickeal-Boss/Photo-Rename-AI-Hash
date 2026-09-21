using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;            // HttpRequestException：重排队分级里用它判定「网络瞬时故障」
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;
namespace PhotoRenameAIHash.Services;

/// <summary>
/// 整理/归档核心流程：扫描 →（AI 识别命名）→ 生成新名 →
/// 目标冲突检测（MD5 相同则跳过，否则按策略加 _1/_2 序号）→ 执行 copy/move/rename
/// → 写重命名日志。全程保证批量操作下文件名的唯一性与可追溯性。
/// </summary>
public sealed class OrganizeService : IOrganizeService
{
    private readonly IPhotoService _photo = AppServices.PhotoService;
    private readonly IHashService _hash = AppServices.HashService;
    private readonly RenameLogService _log = AppServices.RenameLogService;

    private PauseTokenSource? _pts;

    /// <summary>
    /// 「用户点了暂停，但当时暂停令牌还没创建」的待应用标记。
    /// 令牌要到方法内（校验 / 构造 AI / 扫描之前）才 new，在此之前 _pts 为 null，
    /// Pause() 只能把请求记在这里，等创建点补应用（见 <see cref="ApplyPendingPause"/>）。
    /// volatile：写发生在 UI 线程（点暂停），读发生在工作线程与 UI 的 IsPaused 上。
    /// </summary>
    private volatile bool _pendingPause;

    /// <summary>
    /// 当前是否处于暂停状态（供 UI 显示「继续」）。
    /// <b>必须把 <c>_pendingPause</c> 一起算进来</b>：否则用户点暂停只置了待应用标记，本属性仍返回 false
    /// → 界面显示「当前阶段暂不支持暂停」，但稍后创建点把暂停应用上了、循环真的挂起
    /// → 批次已暂停、界面却说不支持（谎报，与 e813471 修掉的是同一类问题）。
    /// </summary>
    public bool IsPaused => _pts?.IsPaused == true || _pendingPause;

    /// <summary>
    /// 协作式暂停：挂起处理循环，保留已扫描的工作队列与状态，可随时继续（区别于 Cancel 的硬取消）。
    /// 令牌尚未创建时（批次未开始、或已开始但仍在校验 / 构造 AI 阶段）先把请求记到
    /// <see cref="_pendingPause"/>，由创建点补应用，避免这一段窗口内点暂停被静默丢弃。
    /// </summary>
    public void Pause()
    {
        if (_pts != null) _pts.Pause();
        else _pendingPause = true;
    }

    /// <summary>继续被暂停的处理循环：同时清掉待应用标记，避免下一批次一开始就莫名处于暂停。</summary>
    public void Resume()
    {
        _pendingPause = false;
        _pts?.Resume();
    }

    /// <summary>
    /// 把「令牌创建之前点下的暂停」补应用到刚创建的令牌上，并清除待应用标记。
    /// 先应用再清标记：否则中间会有一瞬 <see cref="IsPaused"/> 两边都不成立、读成 false。
    /// </summary>
    private void ApplyPendingPause()
    {
        if (!_pendingPause) return;
        _pts?.Pause();           // 状态已由令牌承载，IsPaused 读 _pts.IsPaused 仍为 true
        _pendingPause = false;
    }

    public async Task<OrganizeReport> RunAsync(OrganizeRequest req, IProgress<OrganizeProgress> progress, CancellationToken ct = default)
    {
        var report = new OrganizeReport();
        // 兜底清理：若上一批在「令牌已创建、try 尚未进入」的阶段异常退出（如扫描期间取消），
        // finally 来不及把 _pts 置空。此处清掉陈旧令牌，避免它让 IsPaused 在新批次开始前误报 true。
        // 注意：这里<b>不清</b> _pendingPause——用户可能在进入本方法之前就点了暂停，那份请求
        // 要留到创建点补应用；清掉会让界面（已按 pending 显示「已暂停」）与循环实际行为再次对不上。
        _pts = null;
        // 日志服务是进程级单例、失败计数跨批次累加，故记录批次开始时的基线，
        // 只统计本批次新产生的写入失败，避免把历史累计值报给用户。
        int logFailBefore = _log.FailedWrites;
        string output = req.Mode == OperationMode.Rename ? req.SourceFolder : req.OutputFolder;

        if (string.IsNullOrWhiteSpace(req.SourceFolder) || !Directory.Exists(req.SourceFolder))
        {
            progress.Report(new OrganizeProgress { Message = "源文件夹无效。" });
            // 早退发生在 _pts 创建之前、try/finally 不会执行：必须在这里清掉待应用标记。
            // 否则它会残留到下一批次（用户没点过暂停，下一批一创建令牌却被应用 → 一启动就暂停）。
            _pendingPause = false;
            return report;
        }

        if (req.Mode != OperationMode.Rename &&
            (string.IsNullOrWhiteSpace(output) || !Directory.Exists(output)))
        {
            progress.Report(new OrganizeProgress { Message = "输出文件夹无效。" });
            // 同「源文件夹无效」：早退不会经过 try/finally，此处必须清掉待应用标记，避免污染下一批次。
            _pendingPause = false;
            return report;
        }

        // 运行指纹：把「影响输出文件名 / 目标路径」的参数固化成一个字符串，写入 rename_log.csv。
        // 续传索引只采纳指纹相同的记录——改模板 / 换模式 / 换冲突策略后重跑不再被旧记录静默跳过。
        // conditional: true = 只纳入真正会改变文件名的参数（换引擎但模板不用 AI 占位符时不算变更）。
        req.Fingerprint = ComputeFingerprint(req, output, conditional: true);
        // 读侧一并接受上一代指纹（无条件口径）：否则算法一升级，老用户的所有历史记录立刻全部失配，
        // 每次升级都要被迫全量重做一遍。参数真变了才会两代都不匹配 → 那时重做才是应该的。
        string legacyFingerprint = ComputeFingerprint(req, output, conditional: false);

        IImageAnalysisService? ai;
        try
        {
            ai = CreateAi(req);
        }
        catch
        {
            // 与 :87-89「源文件夹无效」早退同源：构造 AI 失败是 throw 而非 return，发生在
            // _pts 创建之前、try/finally 不会执行，若不在此清掉待应用标记，它会残留到下一批次
            // → 用户没点过暂停，下一批一创建令牌却被应用 → 一启动即暂停。
            _pendingPause = false;
            _pts = null;
            throw;
        }

        // 闸门上限（张/分）：null = 该引擎不带闸门（不主动限速）。
        // 只在批次开始时取一次：档位由 (引擎, 端点, RPM 上限) 三者决定，批次内不会变。
        // 用途是给 UI 显示「闸门上限 N」——N 是上限保护，不是速率目标。
        // P1-3 / P1-3b 配套：本批次不会发任何 AI 请求时（模板不含 AI 占位符，或模拟运行）取 null，
        // 否则界面会显示一个本批次根本不存在的「闸门上限」，与「模拟口径=实跑口径」同样相悖。
        int? gateRpm = TemplateWillUseAi(req)
            ? AiProviderProfiles.For(req.AiProvider, req.CustomApiUrl, req.CustomApiRpmLimit).GateRpm
            : null;

        // 暂停令牌必须在扫描之前创建：此前它在处理循环前才 new，而 Pause() 在 _pts == null 时是
        // 静默 no-op，导致「开始后的扫描 / 加载索引窗口内点暂停」完全失效、UI 却谎报已暂停。
        // 上移后这两个窗口内点暂停即可生效：扫描本身不检查暂停，扫描一结束、处理任何文件之前就挂起。
        _pts = new PauseTokenSource();
        ApplyPendingPause(); // 补应用「令牌创建之前」（校验 / 构造 AI 阶段）点下的暂停
        IReadOnlyList<PhotoFile> files;
        try
        {
            files = await _photo.ScanAsync(req.SourceFolder, ct).ConfigureAwait(false);
        }
        catch
        {
            // 同上：扫描失败（取消 / 目录不可访问）也是「令牌刚创建、try/finally 尚未进入」的窗口，
            // 必须在这里清掉待应用标记与令牌，否则下一批一启动就处于暂停态。
            _pendingPause = false;
            _pts = null;
            throw;
        }
        report.Total = files.Count;

        // 断点续传：读取输出目录（含递归子文件夹）的重命名日志，跳过「已按目标格式命名完成」的文件，
        // 避免重复处理（例如已正确命名的 game_古建筑竞技场_..._screenshot.png）。
        // 第二参数是指纹：历史记录的指纹与本次不同（或旧日志根本没有该列）则一律不计入索引，
        // 宁可重做——重做的最坏结果是「内容相同则跳过 / 加序号」，而静默跳过是用户完全无感的丢活。
        var completed = await _log.LoadRenameLogAsync(output, req.Fingerprint, legacyFingerprint).ConfigureAwait(false);

        // 工作队列：文件处理失败（如视觉模型偶发未按 JSON 返回、网络抖动、瞬时限流等）不直接跳过，
        // 而是重新入队到队尾稍后再次尝试，最大化「成功重命名」的比例；达到单文件最大尝试次数仍失败才放弃。
        var queue = new Queue<(PhotoFile File, int Index)>();
        int order = 0;
        int skippedAtStart = 0;
        foreach (var f in files)
        {
            order++;
            string curName = f.Name; // 当前文件名（含扩展名）
            // 续传跳过：源路径已成功处理（精确匹配，任意模式都安全）优先；
            // 目标匹配仅在「重命名」模式下作为辅助（重命名后文件的当前路径即历史记录里的 NewPath，
            // 避免重跑时把已改好名的文件再改一遍）。
            // Copy/Move 模式不依赖目标匹配，避免新加入且恰与旧目标同名的文件被误跳。
            //
            // P1-2：<b>必须用绝对路径（NewPath）而不是纯文件名（NewName）比对</b>。
            // 扫描是递归的，用纯文件名时「另一个子目录里恰好同名的新文件」会被误判成已处理
            // → 永久静默跳过（每批都写「已完成」记录，于是它永远跳不过来）。
            // 「当前路径 == 某条历史记录的目标路径」才是精确的「本文件已被本规则处理过」判据。
            bool doneBySource = completed.DoneBySource.Contains(f.Path);
            bool doneByTarget = req.Mode == OperationMode.Rename && completed.DoneByNewPath.Contains(f.Path);
            if (doneBySource || doneByTarget)
            {
                skippedAtStart++;
                report.Skipped++;
                // B-06②：续传跳过同样产出结果行，避免「跳过」只在汇总里计数、结果列表却缺行
                var skipEntry = new RenameLogEntry
                {
                    OriginalPath = f.Path,
                    OriginalName = f.Name,
                    Operation = OpName(req.Mode),
                    Status = "跳过(日志已完成)",
                    Fingerprint = req.Fingerprint,
                };
                report.Results.Add(skipEntry);
                // 续传跳过也要带 Percent：OrganizeProgress.Percent 默认 0，VM 无条件赋值，
                // 不带就会把进度条打回 0%。此处 done 尚未声明，用同源的 skippedAtStart 计数。
                progress.Report(new OrganizeProgress
                {
                    Percent = (int)(100.0 * skippedAtStart / Math.Max(1, files.Count)),
                    Result = skipEntry,
                    LogLine = $"{curName} 跳过(日志已完成)",
                });
                continue;
            }
            queue.Enqueue((f, order));
        }

        // 索引可信度告警（放在建队之后：Percent 用同源的 skippedAtStart 计数）。
        // 必须带 Percent：VM 无条件赋值 Progress = p.Percent，不带会把进度条打回 0。
        if (completed.IgnoredByFingerprint > 0)
        {
            progress.Report(new OrganizeProgress
            {
                Percent = (int)(100.0 * skippedAtStart / Math.Max(1, files.Count)),
                LogLine = $"检测到 {completed.IgnoredByFingerprint} 条历史记录来自不同的命名配置" +
                          "（模式 / 冲突策略 / 命名模板，以及该模板实际用到的识别引擎、模型、语言、日期来源中至少一项与本次不同），" +
                          "已忽略这些记录并重新处理对应文件。若你本就是想换规则重跑，此提示可忽略；" +
                          "若希望继续沿用旧记录续传，请保持这些参数不变。",
            });
        }
        if (completed.EnumerationIncomplete)
        {
            progress.Report(new OrganizeProgress
            {
                Percent = (int)(100.0 * skippedAtStart / Math.Max(1, files.Count)),
                LogLine = "警告：续传索引可能不完整（部分子目录无法访问），已按可读取的部分处理。",
            });
        }

        var attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const int maxPerFileAttempts = 10; // 单文件最大尝试次数：网络/超时类瞬时故障沿用用户已确认的口径
        const int maxDeterministicAttempts = 2; // 其余「确定性失败」的重试上限（见下方 catch 的分档说明）

        // 本批次已实际落地的目标路径：ResolveTargetAsync 据此拒绝 Overwrite 覆盖本批次自己产出的文件。
        // 只按批次存活、不做实例字段：本类是单例，实例字段会在并发批次之间互相污染。
        var claimedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 本批次已备份过的源文件路径（重命名模式的保险备份）。
        // 重排队会让同一文件被处理多次，此前每次都会再拷一份备份 → 单文件最多 10 份冗余备份
        // （既占用备份盘空间，也让用户误以为备份了 10 个不同文件）。按批次去重，只备份首次。
        var backedUpThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 连续命中「不可恢复的识别错误」的文件数。达到阈值说明该问题（账户欠费 / 额度耗尽 /
        // 模型不存在 / 参数非法）对整批都成立，继续只会产生 N 次必然失败的请求与 N 行同因错误。
        int consecutivePermanent = 0;

        // 「同因计数」的两条并列状态（与 consecutivePermanent 并列，任一达到 3 即中止整批）。
        // 它补的是 consecutivePermanent 覆盖不到的整批级根因：IsBatchLevel4xx 对 400/422 只在命中
        // 智谱 1211/1214 时判「整批级」，其余厂商（通义欠费 400 Arrearage、OpenAI / 自建网关模型名写错）
        // 会被判成「逐文件级」→ consecutivePermanent 永不累加 → 1000 张的目录会连发 1000 次真实请求
        // 与 1000 行同因错误，而熔断本应在第 3 个就停。
        // 故这里不看 IsBatchLevel，只看「原因是不是同一个」。
        string lastPermanentCause = "";
        int sameCauseCount = 0;

        // A-01：重试会把同一文件重新入队，但重试只应针对「AI 之后」的失败（目标被占用、
        // 备份失败、路径异常等）。缓存成功结果，避免单文件最坏 10 次重复计费与请求放大；
        // AI 自身失败（异常）不入缓存，重试仍会重新请求（瞬时故障需要重试）。
        var aiCache = new Dictionary<string, ImageAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var md5Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 本批次的 AI 观测统计（实际速率 / 单张响应时长）：只为 UI 展示，不参与任何控制流判断。
        // 局部实例而非字段：本类是进程级单例，字段会把上一批的样本带进下一批（跨批次污染 → 谎报）。
        var aiStats = new AiRunStats();

        int done = skippedAtStart; // 已跳过的也算进度推进

        try
        {
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                await _pts.WaitWhilePausedAsync(ct).ConfigureAwait(false); // 协作式暂停：挂起直到继续或取消
                var (f, index) = queue.Dequeue();
                try
                {
                    var entry = await ProcessOneAsync(f, req, output, ai, aiStats, index, ct, aiCache, md5Cache, claimedThisRun, backedUpThisRun).ConfigureAwait(false);
                    Categorize(report, entry);
                    consecutivePermanent = 0; // 成功处理即重置：仅「连续」失败才熔断，容忍偶发假阳性
                    // 同因计数一并清零：中止文案写的是「连续 3 个文件出现相同原因的错误」，
                    // 若不在成功时清零，散落在整批里的同类失败（如偶然的 413）会累积成中止，
                    // 既与文案不符，也会误杀一批本来能跑完的任务。
                    lastPermanentCause = "";
                    sameCauseCount = 0;
                    done++;
                    progress.Report(new OrganizeProgress
                    {
                        Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                        Result = entry,
                        LogLine = $"{entry.OriginalName} -> {entry.NewName} [{entry.Status}]",
                        // 速率三件套只在此处（成功处理一个文件后）刷新：
                        // 失败/重排队路径不该刷——那会拿失败尝试去算速率。
                        AiRpm = RecentAiRpm(aiStats.Stamps),
                        AiLatencyMs = aiStats.LastLatencyMs > 0 ? aiStats.LastLatencyMs : (int?)null,
                        AiGateRpm = gateRpm,
                    });
                }
                catch (PermanentOperationException ex)
                {
                    // 永久性错误（AI 欠费 / 额度耗尽 / 无权限 / 模型不存在，或备份盘满 / 备份权限未授予）：
                    // 重试无法恢复，直接标记失败且不再入队，避免对同一文件刷 10 行「重试」并白白占用 UI 进度。
                    report.Failed++;
                    done++;
                    var permEntry = new RenameLogEntry
                    {
                        OriginalName = f.Name,
                        Status = "错误",
                        Message = ex.Message,
                    };
                    report.Results.Add(permEntry);
                    progress.Report(new OrganizeProgress
                    {
                        Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                        Result = permEntry,
                        LogLine = $"{f.Name} [错误] {ex.Message}",
                    });

                    if (ex.IsEnvironmentError)
                    {
                        // 环境级错误对整批文件都成立（如备份盘已满、权限未授予）：继续处理后续文件无意义，
                        // 直接中止整批并向上报告，由 VM 显示明确提示。已成功处理的文件保持已处理状态，不回滚。
                        // 中止前先报告进度：否则 throw 会跳过末尾的完成汇总，用户不知已处理了多少。
                        progress.Report(new OrganizeProgress
                        {
                            Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                            LogLine = $"已中止：{ex.Message}（此前已处理 {done} 个，共 {files.Count} 个）",
                        });
                        throw;
                    }

                    // 同因计数：**无论 IsBatchLevel 是 true 还是 false 都参与**。
                    // 理由：「逐文件级」只说明「该错误对当前这一张图成立」，并不排除「整批都因同一个
                    // 配置 / 账户根因失败」——通义欠费（400 Arrearage）、自建网关模型名写错都是这个形态，
                    // 却被 IsBatchLevel4xx 判成了逐文件级，于是原来的 consecutivePermanent 永不累加。
                    //
                    // 原因键的取法（决定会不会误触发，改动前务必先读）：
                    //  · 整批级 → 把当前文件名从文案里抹成 "{file}"。整批级文案通常不含文件名，
                    //    抹一下是防将来某条文案内嵌了路径，导致「每个文件的键都不同、永远凑不满 3」。
                    //  · 逐文件级 → 保留原文案。逐文件级文案（如「无法解码图片…：IMG_001.HEIC」）自带
                    //    文件名，正是用来区分「不同文件各自的原因」；若也抹掉，三张连号的损坏 HEIC 就会把
                    //    「跳过 3 张」升级成「整批中止」——那是上一轮刚修掉的误杀，绝不能回来。
                    //    （永久错误不重排队，同一文件在批内只出现一次，故保留文件名后这类计数天然到不了 3；
                    //     真正能凑满 3 的只有「文案里不含文件名的整批级根因」。）
                    //
                    // 边界加固（只嵌文件名还不够）：文案里的文件名<b>不含目录</b>，而相机导出的目录
                    // 天然是同名文件成组出现（DCIM/101/IMG_0001.jpg、DCIM/102/IMG_0001.jpg…），
                    // 深度优先枚举下它们会连续被处理。若整目录被云盘同步 / 杀软锁住，
                    // 三个同名文件的 cause 会完全相同 → 误触发整批中止，且中止文案会谎报成
                    // 「配置 / 账户级问题」。故文案里出现文件名时，再补上完整路径让它与文件一一对应。
                    // <b>不能无脑给所有逐文件级都补路径</b>：那会让「文案不含文件名的整批级根因」
                    // （400 Arrearage 欠费等）也变得人人不同 —— 正好把这第二条闸的作用完全抵消掉。
                    string fileName = Path.GetFileName(f.Path);
                    string cause = ex.IsBatchLevel
                        ? ex.Message.Replace(fileName, "{file}")
                        : (ex.Message.Contains(fileName) ? ex.Message + " @" + f.Path : ex.Message);
                    if (cause == lastPermanentCause) sameCauseCount++;
                    else { lastPermanentCause = cause; sameCauseCount = 1; }

                    // 仅「对整批成立」的永久错误才累计 consecutivePermanent（原有口径原样保留：
                    // 逐文件级有意「既不累加也不归零」——不累加 → 单个坏文件不触发熔断；
                    // 不归零 → 不掩盖此前已累积的整批级证据，4xx → 4xx → 解码失败 → 4xx 仍会中止）。
                    if (ex.IsBatchLevel) consecutivePermanent++;

                    // 两条闸并列，任一触发即中止整批。先判 consecutivePermanent：它是有明确证据的
                    // 「整批级」判据，文案也更具体，保持既有行为与文案不变。
                    if (consecutivePermanent >= 3)
                    {
                        // 非环境的永久错误（AI 账户/额度/模型配置类）按既定口径仍记单文件失败，
                        // 但若连续多个文件都命中，说明是整批级根因：提前中止，避免刷满 N 行同因错误。
                        // 不改变单次调用内的退避重试（供应商策略档：默认最多 8 次 / 总预算 180s）
                        // 与单文件重排队（10 次）的既定口径，
                        // 只把「整批继续」的终止时机提前。
                        progress.Report(new OrganizeProgress
                        {
                            Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                            LogLine = $"已中止：连续 {consecutivePermanent} 个文件命中不可恢复的识别错误（{ex.Message}）" +
                                      $"（此前已处理 {done} 个，共 {files.Count} 个）。请处理账户/额度或模型配置后重跑。",
                        });
                        throw;
                    }

                    if (sameCauseCount >= 3)
                    {
                        // 文案里的 {cause} 用的是「抹掉文件名后」的稳定原因：既便于用户一眼看出根因，
                        // 也避免把同一个文件名重复念三遍（原始 ex.Message 已在上面逐文件报过一行）。
                        progress.Report(new OrganizeProgress
                        {
                            Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                            LogLine = $"已中止：连续 {sameCauseCount} 个文件出现相同原因的错误：{cause}。" +
                                      "这通常是配置/账户级问题（端点、模型名、密钥、额度），" +
                                      "已中止剩余处理以避免继续计费。" +
                                      $"此前已处理 {done} 个，共 {files.Count} 个。",
                        });
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    // 取消立即向上传播，不进入重试逻辑。
                    // P1-4：必须带 ct.IsCancellationRequested 过滤——与 PhotoService.ScanAsync /
                    // ImageAnalysisHelper 的同仓写法一致。不加过滤时，任何<b>非用户取消</b>来源的
                    // OCE（未走 AI 层包装的 HttpClient 超时、Task.Run(…, ct) 的取消态、第三方库超时）
                    // 也会被直接抛出：末尾两条汇总报告被跳过，上层还显示「已取消。」
                    // ——用户从未点过取消，界面却谎报，且剩余文件一行错误都没有。
                    // 加了过滤后，这类 OCE 走正常的分档 + 重排队/记错误路径，与其它确定性失败一致。
                    if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                    attempts.TryGetValue(f.Path, out int n);
                    n++;

                    // 重排队分档：只有「网络 / 超时」与「AI 结果不可用」这两类配得上 10 次重排队
                    // （用户已明确确认过这个口径：AI 侧单次调用内最多退避 8 次、总预算 180s，
                    //  叠加 10 次重排队 = 最坏 80 次请求/文件、约 40 分钟，是既定取舍，不动）。
                    // 它们都不是确定性的：网络抖动会过去，模型采样换个种子可能就合规。
                    // 而「确定性失败」（文件被占用、无权限、MD5 读不出、路径非法、解码失败…）
                    // 上重排队纯属空转：同样的异常必然再抛一次，还要再付一遍 AI 请求。
                    // 故其余异常降到 2 次（给一次「也许刚释放了锁」的机会，然后放弃）。
                    // 注：「配置缺失 / 端点不合规」（模型名、端点、Key 为空或非 https）已由 AI 层
                    // 改为抛 AiPermanentException（isBatchLevel: true），在上方
                    // catch (PermanentOperationException) 分支就被截住——既不重排队，也会连续
                    // 3 个文件后中止整批，不再经过这里的分档。
                    int cap = IsTransientFailure(ex)
                        ? maxPerFileAttempts
                        : maxDeterministicAttempts;

                    if (n < cap)
                    {
                        // 重新排队到队尾，稍后再次尝试（保留原序号，避免重命名序号错乱）
                        attempts[f.Path] = n;
                        queue.Enqueue((f, index));
                        progress.Report(new OrganizeProgress
                        {
                            // 重试不算完成（done 未递增），沿用当前百分比即可：不给值会被 VM 打回 0%
                            Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                            LogLine = $"{f.Name} [重试 {n}/{cap}] 上次失败：{ex.Message}",
                        });
                    }
                    else
                    {
                        // 已达单文件最大尝试次数：放弃该文件，标记为「错误」且不重命名，避免无限循环
                        report.Failed++;
                        done++;
                        var err = new RenameLogEntry
                        {
                            OriginalName = f.Name,
                            Status = "错误",
                            // 终态必须自带「已尝试 N 次」：确定性失败的重排队上限由 10 降到 2 是
                            // 用户可感知的行为变更，而这类异常的 Message 本身不带次数
                            // （AI 网络类异常的文案自带「已尝试 8 次 / 累计 123s」，这类没有），
                            // 不补这一句的话用户只看到一句孤零零的错误，无从判断「是不是已经试过了」。
                            // 用 n（真实尝试次数）而非 cap：分档会随异常类型变化，中途降档时 n 可能 > cap。
                            Message = $"（已尝试 {n} 次）{ex.Message}",
                        };
                        report.Results.Add(err);
                        progress.Report(new OrganizeProgress
                        {
                            Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                            Result = err,
                            LogLine = $"{f.Name} [错误]（已尝试 {n} 次）{ex.Message}",
                        });
                    }
                }
            }
        }
        finally
        {
            _pts = null; // 正常结束或熔断中止都确保清理，避免残留 PauseTokenSource
            _pendingPause = false; // 同理清掉待应用标记，避免污染下一批次
        }

        // 写盘失败此前完全静默：磁盘满时用户会以为已全部记录，实际审计与续传索引已中断。
        int logFailed = _log.FailedWrites - logFailBefore;
        if (logFailed > 0)
        {
            progress.Report(new OrganizeProgress
            {
                // 必须带 Percent：OnProgress 是 Progress = p.Percent 无条件赋值，
                // 不带则会把已完成到 100% 的进度条打回 0（本方法末尾的完成汇总才会再设回 100）。
                Percent = 100,
                LogLine = $"警告：本批次有 {logFailed} 条重命名日志写入失败（常见原因为磁盘空间不足或目录不可写），" +
                          "请检查输出目录与磁盘剩余空间——rename_log.csv 是撤销与续传的唯一索引。",
            });
        }

        progress.Report(new OrganizeProgress
        {
            Percent = 100,
            Message = $"完成：处理 {report.Processed}，跳过 {report.Skipped}（含续传跳过 {skippedAtStart}），失败 {report.Failed}。",
        });
        return report;
    }

    public async Task<OrganizeReport> ArchiveByDateAsync(OrganizeRequest req, IProgress<OrganizeProgress> progress, CancellationToken ct = default)
    {
        var report = new OrganizeReport();
        // 同 RunAsync：清掉上一批可能残留的暂停令牌，避免 IsPaused 在新批次开始前误报 true。
        // 同 RunAsync：此处不清 _pendingPause（用户可能在进入本方法之前已点暂停，需留到创建点补应用）。
        _pts = null;
        // 与 RunAsync 同口径：失败计数是进程级单例的累计值，取基线后只统计本批次新增。
        int logFailBefore = _log.FailedWrites;

        if (string.IsNullOrWhiteSpace(req.SourceFolder) || !Directory.Exists(req.SourceFolder))
        {
            progress.Report(new OrganizeProgress { Message = "源文件夹无效。" });
            // 早退发生在 _pts 创建之前、try/finally 不会执行：必须在这里清掉待应用标记。
            // 否则它会残留到下一批次（用户没点过暂停，下一批一创建令牌却被应用 → 一启动就暂停）。
            _pendingPause = false;
            return report;
        }

        if (string.IsNullOrWhiteSpace(req.OutputFolder) || !Directory.Exists(req.OutputFolder))
        {
            progress.Report(new OrganizeProgress { Message = "输出文件夹无效。" });
            // 同「源文件夹无效」：早退不会经过 try/finally，此处必须清掉待应用标记，避免污染下一批次。
            _pendingPause = false;
            return report;
        }

        // 同 RunAsync：运行指纹（归档的目标子目录由「输出目录 + 日期来源」决定，故同样纳入）
        // 归档的目标子目录由「输出目录 + 日期来源」决定，故 isArchive: true —— 日期来源恒为有效维度，
        // 即便命名模板里没写 {yyyy} 之类的占位符（子目录本身已经用了日期）。
        req.Fingerprint = ComputeFingerprint(req, req.OutputFolder, conditional: true, isArchive: true);
        // 与 RunAsync 同：读侧一并接受上一代指纹，避免算法升级导致老用户全量重做。
        string legacyFingerprint = ComputeFingerprint(req, req.OutputFolder, conditional: false, isArchive: true);

        // 本批次已实际落地的目标路径（语义同 RunAsync）
        var claimedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 同 RunAsync：暂停令牌必须在扫描之前创建，否则扫描 / 加载索引窗口内点暂停是静默 no-op。
        _pts = new PauseTokenSource();
        ApplyPendingPause(); // 同 RunAsync：补应用令牌创建之前点下的暂停
        IReadOnlyList<PhotoFile> files;
        try
        {
            files = await _photo.ScanAsync(req.SourceFolder, ct).ConfigureAwait(false);
        }
        catch
        {
            // 与 RunAsync 同源：扫描失败发生在「令牌已创建、try/finally 尚未进入」的窗口，
            // 必须清掉待应用标记与令牌，否则下一批次一启动就处于暂停态。
            _pendingPause = false;
            _pts = null;
            throw;
        }
        report.Total = files.Count;

        // 断点续传：读取输出目录（含递归子文件夹）的重命名日志，跳过已归档完成（源路径已记录）的文件。
        // 同 RunAsync：指纹不一致的历史记录不计入索引（参数变了就该重做）。
        var completed = await _log.LoadRenameLogAsync(req.OutputFolder, req.Fingerprint, legacyFingerprint).ConfigureAwait(false);

        // 索引可信度告警（与 RunAsync 同文案、同口径；Percent 取 0：此时本批次尚未处理任何文件）
        if (completed.IgnoredByFingerprint > 0)
        {
            progress.Report(new OrganizeProgress
            {
                Percent = 0,
                LogLine = $"检测到 {completed.IgnoredByFingerprint} 条历史记录来自不同的命名配置" +
                          "（模式 / 冲突策略 / 命名模板，以及该模板实际用到的识别引擎、模型、语言、日期来源中至少一项与本次不同），" +
                          "已忽略这些记录并重新处理对应文件。若你本就是想换规则重跑，此提示可忽略；" +
                          "若希望继续沿用旧记录续传，请保持这些参数不变。",
            });
        }
        if (completed.EnumerationIncomplete)
        {
            progress.Report(new OrganizeProgress
            {
                Percent = 0,
                LogLine = "警告：续传索引可能不完整（部分子目录无法访问），已按可读取的部分处理。",
            });
        }

        try
        {
            int done = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                await _pts.WaitWhilePausedAsync(ct).ConfigureAwait(false); // 协作式暂停
                // 续传跳过：源路径精确匹配优先；目标匹配仅限重命名模式（避免 Copy/Move 误跳新文件）。
                // 同 RunAsync：用绝对路径（NewPath）而非纯文件名比对，理由见 RunAsync 内注释（P1-2）。
                bool doneBySource = completed.DoneBySource.Contains(f.Path);
                bool doneByTarget = req.Mode == OperationMode.Rename && completed.DoneByNewPath.Contains(f.Path);
                if (doneBySource || doneByTarget)
                {
                    report.Skipped++;
                    done++;
                    // A-05：归档模式的续传跳过也产出结果行，与整理模式口径一致（否则结果列表行数与汇总不符）
                    var skipEntry = new RenameLogEntry
                    {
                        OriginalPath = f.Path,
                        OriginalName = f.Name,
                        Operation = "归档",
                        Status = "跳过(日志已完成)",
                        Fingerprint = req.Fingerprint,
                    };
                    report.Results.Add(skipEntry);
                    progress.Report(new OrganizeProgress
                    {
                        Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                        Result = skipEntry,
                        LogLine = $"{f.Name} 跳过(日志已完成)",
                    });
                    continue;
                }
                RenameLogEntry? entry = null;
                try
                {
                    // 与 RunAsync 同口径：算不出 MD5 必须显式失败，不能退化成空串。
                    // 空串会让 ResolveTargetAsync 的「内容相同」判定恒 false，
                    // 于是同名文件被判为「内容不同」→ 加 _1 生成重复副本（静默占用空间且难以察觉）。
                    string? rawMd5 = await _hash.TryComputeMd5Async(f.Path, ct).ConfigureAwait(false);
                    if (rawMd5 == null)
                        throw new InvalidOperationException(
                            "无法读取文件内容以计算 MD5（文件可能已被删除、被其它程序独占，或位于不可用的网络/云盘位置）：" + f.Path);
                    string md5 = rawMd5;
                    DateTime when = f.LastModified;
                    if (req.UseExifDate)
                    {
                        var ex = _photo.GetDateTaken(f.Path);
                        if (ex.HasValue) when = ex.Value;
                    }

                    string destDir = Path.Combine(req.OutputFolder, when.ToString("yyyy"), when.ToString("yyyy-MM-dd"));
                    if (!req.DryRun)
                    {
                        // P2-5：建目录失败要包装成「环境级」永久错误，让归档也能早停。
                        // 此前它走下方的通用 catch：目标盘写满 / 无权限时，1000 张的目录会刷 1000 行
                        // 同因错误，还要把每张图的 MD5 都白读一遍。整理模式的对等场景
                        // （ExecuteAsync 的 IOException when IsDiskFull）已是环境级中止口径，
                        // 归档这里对齐它——建目录失败对整批都成立，继续处理没有意义。
                        try
                        {
                            Directory.CreateDirectory(destDir);
                        }
                        // 异常变量刻意不叫 ex：外层 try 块里已有 `var ex = _photo.GetDateTaken(...)`，
                        // 同名会在嵌套作用域触发 CS0136。
                        catch (IOException ioEx) when (IsDiskFull(ioEx))
                        {
                            uint hr = unchecked((uint)ioEx.HResult);
                            throw new PermanentOperationException(
                                $"归档失败：创建目标目录时磁盘空间不足（0x{hr:X8}）：{destDir}。" +
                                "请清理磁盘或更换输出文件夹后再试。",
                                isEnvironmentError: true, inner: ioEx);
                        }
                        catch (UnauthorizedAccessException authEx)
                        {
                            // 权限不足（只读属性 / ACL / 受保护目录 / 被策略拒绝）。
                            // 与整理模式 ExecuteAsync 的同类处理不同：那里是逐文件失败（不熔断），
                            // 而归档的目标目录由「输出目录 + 日期」算出，同一个根因对整批都成立，
                            // 故这里按环境级中止，避免刷满 N 行同因错误。
                            throw new PermanentOperationException(
                                $"没有权限创建归档目录：{destDir}。请检查输出文件夹的权限或以管理员身份运行。",
                                isEnvironmentError: true, inner: authEx);
                        }
                    }

                    var (resolved, targetMd5, degraded, degradeReason) = await ResolveTargetAsync(destDir, f.Name, md5, req.Conflict, req.Mode, claimedThisRun, ct).ConfigureAwait(false);
                    if (resolved == null)
                    {
                        entry = new RenameLogEntry
                        {
                            OriginalPath = f.Path,
                            OriginalName = f.Name,
                            Md5 = md5,
                            Operation = "归档",
                            Status = "跳过(已存在)",
                            Fingerprint = req.Fingerprint,
                        };
                    }
                    else
                    {
                        string status = await ExecuteAsync(req, f.Path, resolved, targetMd5, md5, "归档", ct).ConfigureAwait(false);

                        // 与 RunAsync 同口径：登记本批次已落地的目标。
                        // 模拟运行也要登记（此前 `if (!req.DryRun)` 只在实跑登记）：
                        // 不登记会让模拟结果互相「占位失败」，模拟与实跑口径不一致、模拟会多报覆盖。
                        claimedThisRun.Add(resolved);

                        entry = new RenameLogEntry
                        {
                            OriginalPath = f.Path,
                            OriginalName = f.Name,
                            NewPath = resolved,
                            NewName = Path.GetFileName(resolved),
                            Md5 = md5,
                            Operation = "归档",
                            Status = status,
                            Fingerprint = req.Fingerprint,
                        };
                        if (degraded)
                        {
                            entry.Message = degradeReason;
                        }
                        if (!req.DryRun) await _log.AppendRenameLogAsync(destDir, entry).ConfigureAwait(false);
                    }

                    Categorize(report, entry);
                }
                catch (PermanentOperationException ex) when (ex.IsEnvironmentError)
                {
                    // 与 RunAsync 对齐：环境级错误（如归档目标盘写满）对整批文件都成立，继续处理
                    // 只会刷出 N 行同一真因的错误并让用户白等全批跑完。归档模式本身不调用 AI、
                    // 也无重排队，熔断纯粹是为了「早停 + 不刷屏」。
                    // 过滤器保留：非环境的永久错误仍走下方通用 catch 记单文件错误，行为不变。
                    var envEntry = new RenameLogEntry
                    {
                        OriginalName = f.Name,
                        Status = "错误",
                        Message = ex.Message,
                    };
                    report.Failed++;
                    report.Results.Add(envEntry);
                    progress.Report(new OrganizeProgress
                    {
                        Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                        Result = envEntry,
                        LogLine = $"已中止：{ex.Message}（此前已处理 {done} 个，共 {files.Count} 个）",
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    // 与 RunAsync 对齐：用户取消立即向上传播，不记成「错误」。
                    // 同样必须带 ct.IsCancellationRequested 过滤（理由见 RunAsync 内注释，P1-4）：
                    // 否则非取消来源的 OCE 会让归档在 N 个文件处戛然而止且谎报「已取消」。
                    if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                    report.Failed++;
                    entry = new RenameLogEntry
                    {
                        OriginalName = f.Name,
                        Status = "错误",
                        Message = ex.Message,
                    };
                    report.Results.Add(entry);
                }

                done++;
                progress.Report(new OrganizeProgress
                {
                    Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                    Result = entry, // A-05：归档模式同样把结果推给 UI（此前只发日志，结果卡片恒为空态）
                    LogLine = $"{f.Name} 已归档进度 {done}/{files.Count}",
                });
            }
        }
        finally
        {
            _pts = null; // 正常结束或熔断中止都确保清理，避免残留 PauseTokenSource
            _pendingPause = false; // 同理清掉待应用标记，避免污染下一批次
        }

        // 归档模式同样写 rename_log.csv（分散在各日期子目录），写盘失败的静默风险与 RunAsync 相同。
        int logFailed = _log.FailedWrites - logFailBefore;
        if (logFailed > 0)
        {
            progress.Report(new OrganizeProgress
            {
                // 同 RunAsync：此处进度已达 100%，不带 Percent 会被 VM 的无条件赋值打回 0。
                Percent = 100,
                LogLine = $"警告：本批次有 {logFailed} 条重命名日志写入失败（常见原因为磁盘空间不足或目录不可写），" +
                          "请检查输出目录与磁盘剩余空间——rename_log.csv 是撤销与续传的唯一索引。",
            });
        }

        progress.Report(new OrganizeProgress
        {
            Percent = 100,
            Message = $"归档完成：处理 {report.Processed}，跳过 {report.Skipped}，失败 {report.Failed}。",
        });
        return report;
    }

    /// <summary>
    /// 最近 60 秒窗口内完成的真实 AI 请求数（窗口正好 1 分钟，故计数即「张/分」）。
    /// <b>刻意不用闸门自己的记录</b>：闸门在「取名额」时打点，且失败重试也各打一次，
    /// 拿它当实际速率只会偏高——而用户正是被「闸门 30」误导的，这里不能再给一个偏乐观的数。
    /// 样本少于 2 次返回 null：单张图片的速率没有参考价值（首张还常含冷启动 / 建连开销）。
    /// 批次刚起步、窗口未填满时该值偏保守（只是「到目前为止」的计数），随窗口填满逼近真实速率；
    /// 这与闸门同为 60 秒窗口口径，所以能直接和「闸门上限 N」比大小，正是这行 UI 要回答的问题。
    /// </summary>
    private static double? RecentAiRpm(Queue<DateTimeOffset> stamps)
    {
        var now = DateTimeOffset.UtcNow;
        while (stamps.Count > 0 && now - stamps.Peek() >= TimeSpan.FromMinutes(1))
            stamps.Dequeue();
        if (stamps.Count < 2) return null;
        return stamps.Count;
    }

    /// <summary>
    /// 单批次的 AI 观测统计：真实请求的完成时刻（算速率）与最近一次请求耗时（算响应时长）。
    /// 做成类而非用 ref/out 参数：<see cref="ProcessOneAsync"/> 是 async 方法，
    /// C# 不允许 async 方法声明 ref/out 参数（CS1988），只有把可变状态装进对象才能回传。
    /// </summary>
    private sealed class AiRunStats
    {
        /// <summary>本批次真实 AI 请求（未命中缓存、确实发过 HTTP）的完成时刻。</summary>
        public readonly Queue<DateTimeOffset> Stamps = new();

        /// <summary>最近一次真实 AI 请求的端到端时长（毫秒）；0 = 尚无样本。</summary>
        public int LastLatencyMs;
    }

    private async Task<RenameLogEntry> ProcessOneAsync(
        PhotoFile f, OrganizeRequest req, string output, IImageAnalysisService? ai, AiRunStats aiStats, int index, CancellationToken ct,
        Dictionary<string, ImageAnalysisResult> aiCache, Dictionary<string, string> md5Cache,
        HashSet<string> claimedThisRun, HashSet<string> backedUpThisRun)
    {
        // A-01：MD5 结果缓存——重试时复用，避免对同一文件重复计算（大图 MD5 为全文件读取）。
        if (!md5Cache.TryGetValue(f.Path, out var md5))
        {
            // 算不出 MD5 必须显式失败，不能退化成空串：
            // 空串会让 ResolveTargetAsync 的「内容相同」判定（!string.IsNullOrEmpty(md5)）恒 false，
            // 于是与目标同名的文件一律被判为「内容不同」→ AutoRename 下加 _1 生成重复副本。
            // 典型成因：文件已删除 / 被其它程序独占 / 位于不可用的网络或云盘位置。
            string? rawMd5 = await _hash.TryComputeMd5Async(f.Path, ct).ConfigureAwait(false);
            if (rawMd5 == null)
                throw new InvalidOperationException(
                    "无法读取文件内容以计算 MD5（文件可能已被删除、被其它程序独占，或位于不可用的网络/云盘位置）：" + f.Path);
            md5 = rawMd5;
            // 仅在成功算出 MD5 时入缓存（此处必为非空，缓存空串的隐患已在上一步消除）
            md5Cache[f.Path] = md5;
        }

        // 拍摄时间
        DateTime when = f.LastModified;
        if (req.UseExifDate)
        {
            var ex = _photo.GetDateTaken(f.Path);
            if (ex.HasValue) when = ex.Value;
        }

        // 暂停检查点（AI 之前）：单文件里最耗时的就是 AI 调用（退避最长 180s + 若干次 60s HTTP），
        // 只在主循环顶部检查一次的话，用户在这个文件的退避/HTTP 期间点「暂停」，
        // 要等整段跑完回到循环顶部才挂起 —— 期间 UI 已显示「已暂停」、请求却还在发（P33 谎报 + 持续计费）。
        // _pts 可能为 null（归档循环等未创建令牌的路径），故判空。
        if (_pts != null) await _pts.WaitWhilePausedAsync(ct).ConfigureAwait(false);

        // P1-3 / P1-3b：只有「本批次真的会用到 AI 结果」时才发起识别。
        // needsAi = 已创建引擎 && 模板含 AI 占位符 && 非模拟运行（判据见 TemplateWillUseAi）。
        // 此前只要 ai != null 就无条件调用：模板不含 AI 占位符时逐图付费却完全不影响文件名；
        // 模拟运行同样照调不误，而 UI 写的是「仅预览结果」——用户理解为不花钱的预览，实际每张都计费。
        // 不调 AI 时 f.Category / Scene / … 保持原值（空），由 BuildName 的 Ai() 落成带
        // unknown 前缀的占位：模拟运行下还会带上本文件的批次序号（unknown0007 这种形态），
        // 保证同一批的候选名互不撞车——否则默认模板（六个占位符全是 AI 字段）会让每个文件
        // 算出同一个候选名、被一路加 _1/_2/_3，预览的冲突数与实跑完全对不上（详见 BuildName）。
        bool needsAi = ai != null && TemplateWillUseAi(req);

        // AI 识别（结果缓存：重试复用，避免重复计费）
        // 条件里保留 ai != null（needsAi 已蕴含它，但可空流分析只认显式的空值判断，省掉会报 CS8602）
        if (ai != null && needsAi)
        {
            if (!aiCache.TryGetValue(f.Path, out var res))
            {
                // 只在「真正发请求」的分支计时：命中缓存的文件耗时≈0，
                // 计入会让速率虚高、响应时长虚低（P33 谎报）。
                var aiSw = Stopwatch.StartNew();
                res = await ai.AnalyzeAsync(f.Path, req.Language, ct).ConfigureAwait(false);
                aiSw.Stop();
                aiStats.LastLatencyMs = (int)aiSw.Elapsed.TotalMilliseconds;
                aiStats.Stamps.Enqueue(DateTimeOffset.UtcNow);
                if (res != null) aiCache[f.Path] = res;
            }

            if (res != null)
            {
                f.Category = res.Category;
                f.Scene = res.Scene;
                f.People = res.People;
                f.Action = res.Action;
                f.Subtitle = res.Subtitle;
                f.SourceTag = res.Source;
            }
        }

        // 暂停检查点（AI 之后、写文件之前）：接住「AI 调用进行中」点下的暂停。
        // 注意：AI 内部的**退避等待**已可通过 delayAsync 钩子被暂停打断
        //（见 DelayBackoffHonoringPauseAsync），但一次 HTTP 往返本身仍不可中断；
        // 无论哪种情况，暂停都只能在 AnalyzeAsync 返回后于此处真正挂起——
        // 这是有意的取舍：把暂停改成取消会丢弃已扫描的工作队列，违背暂停语义。
        if (_pts != null) await _pts.WaitWhilePausedAsync(ct).ConfigureAwait(false);

        // 生成新名：未配置 AI 引擎时回退到「日期+原名+序号」，避免 unknown_…_unknown 垃圾名
        // 模板为空（用户清空「命名规则」输入框，或 settings.json 中该字段为 null）时回退到默认模板：
        // 否则 BuildName 返回空串，最终文件名只剩扩展名（如 ".jpg"），批量文件还会互相撞名。
        //
        // P1-3 配套：回退判据由「ai != null」改为「本批次是否真会产出 AI 值」。
        // ai 现在在「模板不含 AI 占位符」「模拟运行」两种情况下也为 null（见 TemplateWillUseAi），
        // 沿用 ai != null 会把用户自己写的、根本不需要 AI 的模板（如 {yyyy}_{name}_{n}）
        // 强行换成默认模板 —— 那等于抹掉用户填的命名规则，是比原问题更严重的回归。
        // 只有「模板确实用到 AI 占位符、而本批次拿不到 AI 值」才回退（即引擎未启用）。
        // 模拟运行刻意<b>不</b>回退：按 P1-3b 用带 unknown 前缀的占位填 AI 字段（见 BuildName 的 Ai()，
        // 并带批次序号以保证批内唯一），让用户看清「哪些字段在模拟下没有真实值」，
        // 而不是整个模板被悄悄换掉——后者会让模拟结果与实际运行的命名规则完全不同，预览也就失去意义。
        bool templateNeedsAi = TemplateUsesAny(req.NamingTemplate ?? "", AiPlaceholders);
        bool aiValueUnavailable = templateNeedsAi && !needsAi && !req.DryRun;
        string template = string.IsNullOrWhiteSpace(req.NamingTemplate) || aiValueUnavailable
            ? DefaultNamingTemplate
            : req.NamingTemplate;
        string baseName = BuildName(f, template, when, index, req.DryRun);
        string candidate = baseName + Path.GetExtension(f.Name);

        // P0-1：重命名模式按「文件自身所在目录」定输出目录，而不是一律用源根目录。
        // 扫描是递归的（PhotoService.EnumerateImages），源根目录下的子目录照片也会被枚举进来；
        // 若把它们的 target 一律拼到源根目录，File.Move 会把子目录里的照片搬到根上
        // ——目录树被静默扁平化，而应用内没有撤销功能、rename_log.csv 也不记录目录回退所需的信息。
        // 「重命名」的语义是原地改名，不是「集中到源根目录」，故就地改名。
        // Copy / Move 模式仍是「送到输出目录」，沿用整批唯一的 output。
        //
        // 注意两个<b>不能跟着改</b>的点（改动前务必先读）：
        //  1. 运行指纹（ComputeFingerprint 的 Field(output)）必须继续用整批唯一的根目录值：
        //     若把每个文件各自的目录传进去，同一批会算出 N 个指纹，历史记录与下次运行永远对不上
        //     → 续传永久失效（那是比本缺陷更严重的回归）。
        //  2. 重命名日志仍写源根目录 output（单一索引文件）：LoadRenameLogAsync 本就递归收集，
        //     写根目录更省事，也让「同一批次的所有记录集中可查」。
        string outDir = req.Mode == OperationMode.Rename
            ? (Path.GetDirectoryName(f.Path) ?? output)
            : output;

        // 目标冲突检测（P2-7：一并取回目标 MD5，ExecuteAsync 直接复用，避免重复计算）
        var (resolved, targetMd5, degraded, degradeReason) = await ResolveTargetAsync(outDir, candidate, md5, req.Conflict, req.Mode, claimedThisRun, ct).ConfigureAwait(false);
        if (resolved == null)
        {
            var skip = new RenameLogEntry
            {
                OriginalPath = f.Path,
                OriginalName = f.Name,
                Md5 = md5,
                Operation = OpName(req.Mode),
                Status = "跳过(已存在)",
                Fingerprint = req.Fingerprint,
            };
            if (!req.DryRun) await _log.AppendRenameLogAsync(output, skip).ConfigureAwait(false);
            return skip;
        }

        // 重命名模式实际执行前，先把原文件备份到用户指定的备份文件夹（保险）
        if (req.Mode == OperationMode.Rename && !req.DryRun &&
            !string.IsNullOrWhiteSpace(req.BackupFolder) &&
            !string.Equals(resolved, f.Path, StringComparison.OrdinalIgnoreCase))
        {
            // 同一文件在本批次内只备份一次：重排队会让 ProcessOneAsync 对同一文件跑多遍，
            // 此前每跑一遍就拷一份（最多 10 份冗余备份），既占备份盘也误导用户以为备份了多个文件。
            // 注意：只在备份<b>成功之后</b>才登记，失败时不登记，下次重试仍会再试一次备份。
            if (!backedUpThisRun.Contains(f.Path))
            {
                await BackupOriginalAsync(req.BackupFolder, f.Path, ct).ConfigureAwait(false);
                backedUpThisRun.Add(f.Path);
            }
        }

        string status = await ExecuteAsync(req, f.Path, resolved, targetMd5, md5, OpName(req.Mode), ct).ConfigureAwait(false);

        // 登记本批次已落地的目标：Overwrite 不得再覆盖它（判定见 ResolveTargetAsync）。
        // 模拟运行也要登记（此前只在实跑登记）：模拟不落地就登记看似矛盾，但实跑时该路径同样会被占用，
        // 不登记会让模拟「少算冲突、多报覆盖」，与实跑口径不一致——模拟的价值就在于口径一致。
        claimedThisRun.Add(resolved);

        var entry = new RenameLogEntry
        {
            OriginalPath = f.Path,
            OriginalName = f.Name,
            NewPath = resolved,
            NewName = Path.GetFileName(resolved),
            Md5 = md5,
            Operation = OpName(req.Mode),
            Status = status,
            Fingerprint = req.Fingerprint,
        };
        // 退化提示：Overwrite 撞上本批次自己刚写的文件、或重命名模式下撞上源目录里已存在的
        // 另一个文件时，都会退化为自动重命名，写明原因，避免用户疑惑「为什么多了一个 _1
        // 后缀」，也表明这不是失败。
        if (degraded)
        {
            entry.Message = degradeReason;
        }
        if (!req.DryRun) await _log.AppendRenameLogAsync(output, entry).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// 执行实际的 copy/move/rename。
    /// - 若目标已存在且内容相同(MD5)，视为已存在、不重复写入；
    /// - Overwrite 模式直接覆盖；其余模式目标已处理为唯一名。
    /// <b>重命名模式下 Overwrite 不生效</b>（见下方 overwrite 取值）：原地重命名时目标就在源目录里，
    /// 覆盖等于删除源目录中的另一个文件，而备份只备份 source、不备份 target → 不可恢复的静默丢数据。
    /// P2-7：<paramref name="targetMd5"/> 为 ResolveTargetAsync 阶段算得的目标 MD5
    /// （内容相同时即源 MD5），直接复用其结论，不再重复计算一次目标 MD5。
    /// </summary>
    private async Task<string> ExecuteAsync(OrganizeRequest req, string source, string target, string? targetMd5, string md5, string opName, CancellationToken ct)
    {
        // 入口检查：File.Copy / File.Move 没有 CancellationToken 重载，真正可中断需改写成
        // FileStream.CopyToAsync(dest, 81920, ct)——本轮不做，此处仅保证「已取消时不再动盘」。
        ct.ThrowIfCancellationRequested();

        // ResolveTarget 已判定「目标存在且内容与源相同」时直接复用结论；
        // Overwrite 路径下 targetMd5 是旧内容 MD5 ≠ 源 md5，不会被误判。
        if (File.Exists(target) && !string.IsNullOrEmpty(targetMd5) && targetMd5 == md5)
            return "未改动(内容相同)";

        if (req.DryRun)
            return "模拟(" + opName + ")";

        // 重命名模式下绝不覆盖：原地重命名的 target 落在源目录，覆盖会替换掉源目录里的另一个文件，
        // 而备份（BackupOriginalAsync）只备份 source、不备份 target → 被覆盖的那个文件没有任何副本，
        // 等于静默删除且不可恢复。冲突让 ResolveTargetAsync 加序号解决（Degraded 提示写明改名）。
        bool overwrite = req.Conflict == ConflictStrategy.Overwrite && req.Mode != OperationMode.Rename;
        try
        {
            if (req.Mode == OperationMode.Rename)
            {
                if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
                    return "未改动(内容相同)";
                File.Move(source, target, overwrite);
                return "已重命名";
            }

            if (req.Mode == OperationMode.Copy)
            {
                File.Copy(source, target, overwrite);
                return "已复制";
            }

            File.Move(source, target, overwrite);
            return "已移动";
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            // 写入目标盘空间不足：与备份同源的永久错误。重排队 10 次无意义（每次还可能重复调用 AI），
            // 且必须给出可定位的真因，而不是让 IOException 的原始信息裸奔。
            // 过滤器只接磁盘/内存不足，其余 IOException（如文件被占用）继续向上走重试逻辑，行为不变。
            uint hr = unchecked((uint)ex.HResult);
            throw new PermanentOperationException(
                $"写入失败：目标驱动器磁盘空间不足（0x{hr:X8}）。请清理磁盘或更换输出文件夹后再试。" +
                "（输出目录可能残留不完整文件，请检查。）",
                isEnvironmentError: true, inner: ex);
        }
        catch (IOException ex) when (ex.HResult == ErrorSharingViolation || ex.HResult == ErrorLockViolation)
        {
            // 文件被其它程序占用（看图软件 / 编辑器 / 云盘同步 / 杀软扫描）。
            // 此前落通用 catch → 空转重排队 10 次、且归因文案是裸的 IOException。
            // 这是「关闭占用程序后就能过」的确定性失败：包装成非环境级、非整批级的永久错误，
            // 让它只记当前这一个文件的错误并继续处理其它文件（不熔断整批、不刷 10 行重试）。
            throw new PermanentOperationException(
                $"文件被其它程序占用，无法处理：{Path.GetFileName(source)}。请关闭可能占用它的看图软件/编辑器/云盘同步后重试。",
                isEnvironmentError: false, isBatchLevel: false, inner: ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 权限不足（只读属性、ACL、受保护目录、被策略拒绝）。
            // 同上：逐文件失败、不熔断整批；给出可执行的排障建议而非裸异常文本。
            throw new PermanentOperationException(
                $"没有权限访问：{Path.GetFileName(source)}。请检查文件/文件夹权限或以管理员身份运行。",
                isEnvironmentError: false, isBatchLevel: false, inner: ex);
        }
    }

    // HRESULT_FROM_WIN32：ERROR_DISK_FULL(112) / ERROR_OUTOFMEMORY(14) / ERROR_NOT_ENOUGH_MEMORY(8)。
    // 以负 int 常量比较，避免 0x8xxxxxxx 字面量溢出 int。
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorOutOfMemory = unchecked((int)0x8007000E);
    private const int ErrorNotEnoughMemory = unchecked((int)0x80070008);

    // HRESULT_FROM_WIN32：ERROR_FILE_EXISTS(80) / ERROR_ALREADY_EXISTS(183)。
    // File.Copy(overwrite:false) 撞上已存在目标时抛的 IOException 携带其中之一，
    // 据此区分「换个序号重试」与「真的写失败（如磁盘满）」。
    private const int ErrorFileExists = unchecked((int)0x80070050);
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    // HRESULT_FROM_WIN32：ERROR_SHARING_VIOLATION(32) / ERROR_LOCK_VIOLATION(33)。
    // 文件被其它进程独占或锁定（看图软件 / 编辑器 / 云盘同步 / 杀软）时由 File.Move / File.Copy 抛出。
    // 与「磁盘满」区分：前者关掉占用程序即可恢复，后者必须清理空间，提示语必须不同。
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>备份文件名去重的最大尝试次数（原名 + _1…_9999），与 <see cref="SuffixUntilFreeAsync"/> 的 9999 上限一致。</summary>
    private const int MaxBackupNameAttempts = 9999;

    /// <summary>
    /// 重命名模式实际执行前的保险：把原文件复制一份到备份文件夹。
    /// 走 <see cref="System.IO"/> 而不是 StorageFolder / StorageFile：<c>GetFolderFromPathAsync</c> 这类
    /// <b>路径式</b> WinRT 访问受 <c>broadFileSystemAccess</c> 能力门控，而该能力<b>默认 Off</b>，
    /// 未手动授权的设备上备份必然失败（同文件 ExecuteAsync 一直走 System.IO、始终正常，
    /// 于是用户看到「整理能用、备份不能用」）。full trust（mediumIL）下的 System.IO 不受该门控，
    /// 也不依赖 FolderPicker 的 Broker 令牌。
    /// 失败一律抛 <see cref="PermanentOperationException"/>（IsEnvironmentError=true）：备份失败是
    /// 阻断性的、重试无意义，由 <see cref="RunAsync"/> 标记「错误」且不执行 rename，避免丢失原文件；
    /// 同名冲突由 <see cref="CopyWithUniqueName"/> 自行追加 _1/_2 序号（等价于原
    /// <c>NameCollisionOption.GenerateUniqueName</c>），且<b>永不覆盖</b>已有备份。
    /// 磁盘空间不足单独识别并给出与「路径不可写」不同的提示，避免误导用户排障。
    /// </summary>
    private static async Task BackupOriginalAsync(string backupDir, string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 事前空间检查：等 File.Copy 写到一半才失败既浪费一次尝试，又可能在备份目录留下不完整文件。
        // 检查本身任何异常都降级为「跳过检查」（见 EnsureEnoughFreeSpace），绝不能阻断备份。
        EnsureEnoughFreeSpace(backupDir, source);

        try
        {
            // File.Copy 是同步 API，会阻塞当前线程。调用链已全程 ConfigureAwait(false)，此处不在 UI 线程，
            // 包 Task.Run 只为不在编排循环所在线程上做阻塞 I/O（并保持本方法 async 签名，避免改动调用点）；
            // 代价：拷贝一旦开始便无法中断——同步 File.Copy 没有取消通道，这是相对原 WinRT 版本的能力退化。
            await Task.Run(() => CopyWithUniqueName(backupDir, source, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消语义不得被包装，否则 TaskCanceledException 会被当成失败吞掉
        }
        catch (Exception ex)
        {
            // 磁盘空间不足：与「权限未授予 / 路径不可访问」区分开，给出可定位的真因
            if (IsDiskFull(ex))
            {
                uint hr = unchecked((uint)ex.HResult);
                throw new PermanentOperationException(
                    $"备份失败：写入时磁盘空间不足（0x{hr:X8}）。请清理磁盘或更换备份文件夹后再试。" +
                    "（备份目录可能残留不完整文件，请检查。）",
                    isEnvironmentError: true, inner: ex);
            }

            // 其余失败（备份文件夹不存在 / 路径不可访问 / 文件被占用等）：
            // 改走 System.IO 后本路径已不受 broadFileSystemAccess 门控，故不再引导用户去授予
            // 「文件系统」访问权限——那会让他去找一个与本次失败无关的开关。
            throw new PermanentOperationException(
                "备份原始文件失败（请确认备份文件夹存在且当前用户可写入，且源文件未被其它程序独占）：" + ex.Message,
                isEnvironmentError: true, inner: ex);
        }
    }

    /// <summary>
    /// 把 <paramref name="source"/> 复制到 <paramref name="backupDir"/>，目标名冲突时按 _1/_2 递增，
    /// 直到拿到一个空位（风格与 <see cref="SuffixUntilFreeAsync"/> 一致）。
    /// </summary>
    /// <remarks>
    /// <b>不做 File.Exists 预检、直接 Copy(overwrite:false)：</b>「先判定再拷贝」两步非原子（TOCTOU），
    /// 同名文件在备份目录里极常见（<c>IMG_0001.jpg</c> 之类），若将来并发化或跑两个实例，
    /// 两个线程会同时判定「不存在」然后互相覆盖——备份是用户最后的保险，覆盖等于销毁历史备份。
    /// 改为捕获「已存在」的 IOException 后换下一个序号重试，把「查」与「占」合成一次原子操作。
    /// 判定为「已存在」才换号重试，磁盘满等其它 IOException 立即上抛，交由上层
    /// <see cref="IsDiskFull"/> 判定，否则会对一个注定失败的写入空转近万次
    /// （判定细则见 <see cref="IsAlreadyExists"/>）。
    /// </remarks>
    private static void CopyWithUniqueName(string backupDir, string source, CancellationToken ct)
    {
        string stem = Path.GetFileNameWithoutExtension(source);
        string ext = Path.GetExtension(source);
        string dest = Path.Combine(backupDir, Path.GetFileName(source));

        // P2-1：上界是 MaxBackupNameAttempts + 1。
        // 循环内第 i 次「先拷 dest、失败后再赋 stem_i」，故循环跑满 N 次时实际尝试的是
        // 原名 + _1…_{N-1}；写成 i <= MaxBackupNameAttempts 的话最后一次赋值出的
        // _9999 从未被 File.Copy 尝试过 —— 平白少一个候选，且与 SuffixUntilFreeAsync
        // （循环结束后补查 _9999）的口径不一致。+1 后尝试集合为 原名 + _1…_9999，与文案自洽。
        for (int i = 1; i <= MaxBackupNameAttempts + 1; i++)
        {
            ct.ThrowIfCancellationRequested(); // 只能在两次尝试之间检查：拷贝过程本身不可中断
            try
            {
                // overwrite 恒为 false：备份目录里的同名文件是历史备份，覆盖即销毁
                File.Copy(source, dest, overwrite: false);
                return;
            }
            catch (IOException ex) when (IsAlreadyExists(ex, dest))
            {
                dest = Path.Combine(backupDir, $"{stem}_{i}{ext}");
            }
        }

        throw new IOException(
            $"备份目录中没有可用文件名（{Path.GetFileName(source)} 及其 _1…_{MaxBackupNameAttempts} 后缀均已被占用）。");
    }

    /// <summary>
    /// 判断一次失败的 <see cref="File.Copy(string,string,bool)"/> 是否只是「目标已存在」（该换下一个序号），
    /// 而不是真的写失败（如磁盘满，那必须立即上抛，否则会对注定失败的写入空转近万次）。
    /// </summary>
    /// <param name="ex">拷贝抛出的 IOException。</param>
    /// <param name="dest">本次尝试的目标路径。</param>
    private static bool IsAlreadyExists(IOException ex, string dest)
    {
        // 首选：Win32 错误码（ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS）——与既有 IsDiskFull 同一套机制
        if (ex.HResult == ErrorFileExists || ex.HResult == ErrorAlreadyExists)
            return true;

        // P1-1：兜底之前必须先排除「磁盘 / 内存不足」。
        // 备份目录在 UNC 共享或配额受限卷上时，EnsureEnoughFreeSpace 会按设计降级跳过
        // （见该方法注释：UNC / 无盘符 / DriveInfo 不可用一律放行），于是 File.Copy 可能
        // 已创建目标文件、写入一部分之后才失败（0x80070070）并留下一个不完整文件。
        // 此时若直接 File.Exists 兜底：partial 文件存在 → 误判成「目标已存在」→ 换 _1…_9999
        // 反复往一张满盘上拷，既把备份盘彻底写满、留下上千个垃圾文件，
        // 又把真因「磁盘空间不足」谎报成「没有可用文件名」（用户会去删备份而不是去清空间）。
        // 判序：HResult（精确）→ IsDiskFull（真写失败）→ File.Exists（兜底）。
        if (IsDiskFull(ex)) return false;

        // 兜底：.NET 也可能抛不带 Win32 HResult 的通用 IOException（HResult = COR_E_IO 0x80131620），
        // 此时上面的判定恒不命中 → 循环第一次就抛出 → 落到「备份失败」永久错误 → 重名备份功能回归。
        // 故在「拷贝已失败」之后再看一次目标是否存在：
        // 这与要规避的 TOCTOU 不是一回事——TOCTOU 是「检查通过后再操作」的窗口，
        // 这里是操作已失败、且失败本身没有副作用需要回滚，只是据此决定下一个名字。
        // 宁可多试一个序号，也不要让重名直接失败（失败方向虽不丢数据，但整批会被阻断）。
        try { return File.Exists(dest); }
        catch { return false; }
    }

    /// <summary>
    /// 备份前的事前空间检查：比较目标驱动器可用空间与源文件大小，留安全余量（1 MB 或源文件大小的 1%，
    /// 取较大者）。空间不足直接抛环境级永久错误，避免 File.Copy 写到一半才失败。
    /// 检查本身任何异常都降级为「跳过检查」——UNC 路径、无盘符、驱动器未就绪、DriveInfo 在 MSIX
    /// 容器下不可用等情况一律放行，让 File.Copy 自己报错；检查绝不能阻断正常的备份流程。
    /// </summary>
    private static void EnsureEnoughFreeSpace(string backupDir, string source)
    {
        try
        {
            string? root = Path.GetPathRoot(backupDir);
            if (string.IsNullOrEmpty(root)) return; // 无盘符（相对路径 / UNC）：无法判定，跳过

            long size = new FileInfo(source).Length;
            long free = new DriveInfo(root).AvailableFreeSpace;

            // 安全余量：1 MB 或源文件大小的 1%，取较大者（小文件也至少留 1 MB，避免恰好写满）
            long margin = Math.Max(1024L * 1024L, size / 100);
            long need = size + margin;
            if (free >= need) return;

            throw new PermanentOperationException(
                $"备份失败：目标驱动器 {root} 可用空间不足（需要 {need:N0} 字节，可用 {free:N0} 字节）。" +
                "请清理磁盘或更换备份文件夹后重试。",
                isEnvironmentError: true);
        }
        catch (PermanentOperationException)
        {
            throw; // 确实空间不足：这是最终结论，必须向上抛出
        }
        catch
        {
            // 其余情况（UNC / 无盘符 / 驱动器未就绪 / DriveInfo 不可用）：跳过检查
        }
    }

    /// <summary>是否为「磁盘或内存不足以完成写入」的 HRESULT。备份写入中途失败的典型真因，
    /// 必须与「权限未授予 / 路径不可访问」区分，否则提示会误导用户排障。</summary>
    private static bool IsDiskFull(Exception ex)
        => ex.HResult == ErrorDiskFull || ex.HResult == ErrorOutOfMemory || ex.HResult == ErrorNotEnoughMemory;

    /// <summary>
    /// 是否值得按「单文件重排队 10 次」的口径重试。只有两类配得上：
    /// <list type="bullet">
    /// <item><description>网络 / 超时类瞬时故障（429/5xx 重试或退避预算耗尽、连接失败、60 秒超时）
    /// ——用户已明确确认过的 10 次口径，不动。</description></item>
    /// <item><description><see cref="AiResultInvalidException"/>：AI 返回了内容但不可用
    /// （响应体不是合法 JSON，或模型输出解析不出结构化结果）。temperature=0.3 下模型输出并非确定性，
    /// 换一次采样可能就合规，网关偶发返回 HTML 错误页也往往是瞬时的——<b>重试有真实成功率</b>，
    /// 与「配置缺失 / 解码失败」这类必然复现的失败不是一回事。</description></item>
    /// </list>
    /// 其余（解码失败、MD5 读不出、文件被占用、无权限、路径非法…）一律 2 次。
    /// 例外：配置缺失 / 端点不合规（模型名 / 端点 / Key 为空，或端点非 https）已由 AI 层改为抛
    /// <see cref="AiPermanentException"/>（<c>isBatchLevel: true</c>），在上层
    /// <c>catch (PermanentOperationException)</c> 就被截住——<b>不重排队</b>，且连续 3 个文件后
    /// 中止整批；它<b>不会</b>流到这里的分档逻辑。
    /// <para><b>判据一（最可靠）：专用类型 <see cref="AiTransientException"/>。</b>
    /// AI 层在「网络 / 连通性重试耗尽」与「60 秒超时」时抛的就是它——不看文案、不看 inner 类型，
    /// 直接按语义判定。补它的原因见判据二。</para>
    /// <para><b>判据二：顺着 InnerException 链找 <c>HttpRequestException</c> / <c>TimeoutException</c>。</b>
    /// 保留这一档有两个用处：① AI 层历史上把这两类包装成裸 <c>InvalidOperationException</c>
    /// （原异常作为 inner 保留），链上找得到就仍按瞬时故障处理，不必依赖改动落地顺序；
    /// ② 顶层直接命中 <c>HttpRequestException</c> 的情况是「429/5xx 重试或退避预算耗尽」，同属可重试口径。
    /// HttpClient 的 60s 超时抛的是 <c>TaskCanceledException</c>，其 <c>InnerException</c> 正是
    /// <c>TimeoutException</c>，顺着链即可归因，不必解析异常文案。</para>
    /// <para>判据二单独用是脆弱的：HttpClient 抛出的原始异常类型随 .NET 版本与底层 IO 实现变化
    /// （可能是 <c>IOException</c> / <c>SocketException</c>），那种情况会被漏判成确定性失败、
    /// 只重排 2 次，违背「网络类宁可慢也不产出 unknown_ 垃圾名」的既定口径。这正是补上判据一的原因。</para>
    /// </summary>
    private static bool IsTransientFailure(Exception ex)
    {
        // 结果不可用：按类型精确判定（AI 层为此新增了该类型，省去「无 inner 的 IOE」这种粗糙反推）
        if (ex is AiResultInvalidException) return true;

        int depth = 0; // 防御：异常链理论上不会自环，但深度上限可杜绝病态构造导致的死循环
        for (Exception? e = ex; e != null && depth < 8; e = e.InnerException, depth++)
        {
            if (e is AiTransientException or HttpRequestException or TimeoutException) return true;
        }
        return false;
    }

    /// <summary>
    /// 计算最终写入目标路径：
    /// - 不存在（且未被本批次占用）→ 直接返回；
    /// - 存在且内容相同(MD5) → 返回该路径（调用方按「未改动」处理）；
    /// - 存在且内容不同 → 按策略：Skip 返回 null（跳过），Overwrite 返回该路径，AutoRename 追加 _1/_2 直到唯一（同样以 MD5 判定是否重复内容）。
    /// P2-7：目标已存在时一并返回其 MD5，供 ExecuteAsync 复用，避免每文件重复计算 MD5。
    /// <paramref name="claimedThisRun"/>：本批次已实际落地的目标路径。Overwrite 撞上其中的路径时
    /// 退化为自动重命名（Degraded=true）——覆盖它会静默销毁本批次已产出的成果，Move 模式等于丢数据。
    /// <paramref name="mode"/>：重命名模式下 Overwrite 一律不得生效（target 在源目录里，
    /// 覆盖会删掉源目录中的另一个文件且无备份），同样退化为自动重命名。
    /// <returns>Degraded：仅当「本应选 Overwrite 但因撞上本批次产出 / 重命名模式而改为加序号」时为 true；
    /// DegradeReason 为该退化的用户可读说明（已含改后的文件名，直接进 RenameLogEntry.Message）。</returns>
    /// </summary>
    private async Task<(string? Target, string? TargetMd5, bool Degraded, string DegradeReason)> ResolveTargetAsync(
        string output, string candidate, string md5, ConflictStrategy conflict, OperationMode mode,
        HashSet<string> claimedThisRun, CancellationToken ct)
    {
        string target = Path.Combine(output, candidate);
        // 「占用」= 磁盘上已存在 或 本批次已登记（含模拟运行：模拟也登记，口径与实跑一致）。
        // 此前只判 File.Exists → 模拟运行时不登记 claimed，于是模拟会比实跑少算冲突、多报覆盖。
        if (!claimedThisRun.Contains(target) && !File.Exists(target)) return (target, null, false, "");

        string existing = await _hash.TryComputeMd5Async(target, ct).ConfigureAwait(false) ?? "";
        if (!string.IsNullOrEmpty(md5) && md5 == existing)
            return (target, existing, false, ""); // 内容相同，无需动作

        // 原写法是 switch 表达式，加入「Overwrite 撞本批次产出 / 重命名模式」的条件分支后需带 when 子句，
        // 故改为 if/else 语句形式，语义等价且分支更直观（Skip / Overwrite / 其余=AutoRename 全覆盖）。
        if (conflict == ConflictStrategy.Skip) return (null, null, false, "");

        if (conflict == ConflictStrategy.Overwrite && mode != OperationMode.Rename && !claimedThisRun.Contains(target))
            return (target, existing, false, "");

        // 走到这里有三种情况：AutoRename，或 Overwrite 但 target 是本批次自己刚写进去的文件，
        // 或 Overwrite 但处于重命名模式（target 在源目录，覆盖会静默删掉另一个文件且没有备份）。
        // 后两者必须退化为自动重命名：覆盖会静默销毁数据，Move 模式尤其致命——
        // 源文件已移出源目录且不做备份，被覆盖的内容没有任何副本，等于直接丢数据。
        var (suffixed, suffixMd5) = await SuffixUntilFreeAsync(output, candidate, md5, claimedThisRun, ct).ConfigureAwait(false);
        bool degraded = conflict == ConflictStrategy.Overwrite;
        string reason = "";
        if (degraded)
        {
            reason = mode == OperationMode.Rename
                ? $"重命名模式下不会覆盖源文件夹中的其它文件，已自动改名为 {Path.GetFileName(suffixed)}"
                : $"目标已被本批次其他文件占用，已自动重命名为 {Path.GetFileName(suffixed)}" +
                  "（Overwrite 不会覆盖本批次已产出的文件，避免静默丢数据）";
        }
        return (suffixed, suffixMd5, degraded, reason);
    }

    /// <summary>序号后缀 _1…_9999 的最大尝试次数。</summary>
    private const int MaxSuffixAttempts = 9999;

    /// <summary>
    /// P2-7：返回最终空位目标路径；窗口内 MD5 命中相同内容时一并传出该目标 MD5。
    /// <paramref name="claimedThisRun"/>：与 <see cref="ResolveTargetAsync"/> 同口径，
    /// 本批次（含模拟运行）已登记的路径也算「已占用」，否则模拟与实跑会给出不同的结果。
    /// </summary>
    private async Task<(string Target, string? TargetMd5)> SuffixUntilFreeAsync(
        string output, string candidate, string md5, HashSet<string> claimedThisRun, CancellationToken ct)
    {
        string name = Path.GetFileNameWithoutExtension(candidate);
        string ext = Path.GetExtension(candidate);
        string target = Path.Combine(output, candidate);

        // 循环内先判空位再换下一个序号：换号与判定一体，末尾不再有「赋值完就退出」的裸分支。
        // 覆盖的候选是 candidate 及其 _1…_9998（循环内 9999 个），循环结束后再补查 _9999：
        // 比原实现<b>多检查了末尾这 1 个候选</b>——原实现正是把它未检查就返回，才导致
        // 后续 File.Move/Copy 必然抛 IOException（AutoRename 下 → 重排队空转）。
        for (int i = 1; i <= MaxSuffixAttempts; i++)
        {
            if (!File.Exists(target) && !claimedThisRun.Contains(target))
                return (target, null); // 拿到空位

            string em = await _hash.TryComputeMd5Async(target, ct).ConfigureAwait(false) ?? "";
            if (!string.IsNullOrEmpty(md5) && md5 == em)
                return (target, em); // 该序号名下已是相同内容

            target = Path.Combine(output, $"{name}_{i}{ext}");
        }

        // 走到这里说明 candidate 与其 _1…_9999 全被占用（含本批次已登记的）。
        // 旧实现在这里直接 return 最后一次赋值的 name_9999——该路径未经存在性检查，
        // 后续 File.Move/Copy 必然抛 IOException（AutoRename 下 → 重排队 10 次空转）。
        // 必须显式失败并说清原因，绝不能返回一个「已知被占用」的路径。
        if (File.Exists(target) || claimedThisRun.Contains(target))
            throw new IOException(
                $"目标文件夹中没有可用文件名（{candidate} 及其 _1…_{MaxSuffixAttempts} 后缀均已被占用），" +
                "请清理输出目录或改用「跳过 / 覆盖」以外的冲突策略。");

        return (target, null);
    }

    /// <summary>最终文件基名长度上限：单字段 40 字符 × 6 字段模板 + 分隔符 + 扩展名 + 目录深度
    /// 可能超过 MAX_PATH（app.manifest 已按既定取舍移除 longPathAware），此处整体收口。</summary>
    private const int MaxBaseNameLength = 180;

    /// <summary>
    /// 命名模板的兜底值（「日期 + 原名 + 序号」）：未配置 AI 引擎、模板为空、
    /// 或模板净化后为空时使用。抽成常量是因为 <see cref="ProcessOneAsync"/> 与
    /// <see cref="BuildName"/> 两处都要用，写两份必然漂移。
    /// </summary>
    private const string DefaultNamingTemplate = "{yyyy}{MM}{dd}_{name}_{n}";

    /// <summary>
    /// 按命名模板生成最终基名（不含扩展名）。
    /// </summary>
    /// <param name="dryRun">
    /// 模拟运行标志：为 true 时 AI 占位符在<b>没有真实值</b>的情况下会带上本文件的批次序号，
    /// 而不是一律填同一个 <c>unknown</c>。理由见方法内 <c>Ai()</c> 的注释（P1-3b 配套）。
    /// 有真实值（非模拟路径）时两者完全等价，实跑行为不受这个参数影响。
    /// </param>
    private static string BuildName(PhotoFile f, string template, DateTime when, int index, bool dryRun)
    {
        string San(string v) => Sanitize(v);

        // P1-3b 配套：模拟运行下 AI 字段没有真实值，占位值必须<b>逐文件可区分</b>。
        // 若一律填同一个 "unknown"：默认模板 {category}_{scene}_{people}_{action}_{subtitle}_{source}
        // 六个占位符全是 AI 字段（不含 {name} / {n}），于是同一批每个文件算出的候选名一字不差
        // → 撞上 claimedThisRun → 一路加 _1 / _2 / _3 … → 预览变成「楼梯」。
        // 而实跑时各文件 AI 结果不同、几乎不撞名，于是预览给出的<b>冲突数、后缀分布、最终文件名
        // 全部与实跑对不上</b>——用户照预览做的判断是错的，违背「模拟口径必须与实跑一致」。
        // 带上批次序号（该文件在队列中的次序，批内唯一）后候选名恢复唯一，假楼梯消失，
        // 冲突口径回到与实跑一致；保留 unknown 前缀，用户一眼能看出是占位、不会误读成真实识别结果。
        // 非模拟路径完全不动，仍是既有的 unknown 兜底。
        string Ai(string v) => string.IsNullOrWhiteSpace(v)
            ? (dryRun ? "unknown" + index.ToString("D4") : "unknown")
            : San(v);

        // 抽成局部函数：末尾「净化后为空」的兜底需要用它再跑一遍默认模板，
        // 不抽就要把 15 行 Replace 抄两遍（抄两份必然漂移）。
        string Build(string tpl) => (tpl ?? "")
            .Replace("{yyyy}", when.ToString("yyyy"))
            .Replace("{MM}", when.ToString("MM"))
            .Replace("{dd}", when.ToString("dd"))
            .Replace("{HH}", when.ToString("HH"))
            .Replace("{mm}", when.ToString("mm"))
            .Replace("{ss}", when.ToString("ss"))
            .Replace("{date}", when.ToString("yyyy-MM-dd"))
            .Replace("{n}", index.ToString("D4"))
            .Replace("{name}", San(Path.GetFileNameWithoutExtension(f.Name)))
            .Replace("{category}", Ai(f.Category))
            .Replace("{scene}", Ai(f.Scene))
            .Replace("{people}", Ai(f.People))
            .Replace("{action}", Ai(f.Action))
            .Replace("{subtitle}", Ai(f.Subtitle))
            .Replace("{source}", Ai(f.SourceTag));

        var built = Build(template);

        // A-06：对最终基名整体截断，避免多字段模板叠加目录深度后触发 PathTooLongException
        if (built.Length > MaxBaseNameLength)
            built = built.Substring(0, MaxBaseNameLength).TrimEnd('_', ' ', '.');

        // P1-5：截断之后再对<b>结果整体</b>做一次「路径级」净化。
        // 上面只对每个占位符的<b>值</b>做了 San()，模板自身的字面量（"../"、"..\..\"、"C:\Windows\"）
        // 是原样保留的：Path.Combine 会把 ".." 解析到输出目录之外，遇到绝对路径甚至按 .NET 语义
        // 直接返回该绝对路径 —— 用户照片被静默写到预期之外的位置（叠加重命名模式的 File.Move 更严重）。
        // 这里统一剥离目录分隔符与盘符，保证 Path.Combine(output, candidate) 的结果永远落在 output 之内。
        // 选中「剥离」而不是「报错拒绝整批」，是为了与既有 Sanitize 口径一致：
        // 非法字符一律替换而非中止（中止会让用户为一个字符重填整个模板并重启批次）。
        built = string.Concat(built.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':'));

        // P1-5 兜底：模板字面量<b>全是</b>分隔符 / 空白（"../"、"\"、"/"、"   "…）时净化结果为空，
        // 直接返回会让文件名只剩扩展名（".jpg"），同批文件还会互相撞名并被一路加 _1/_2/_3。
        // 回退到默认模板，与 ProcessOneAsync 的「模板为空」回退同口径（同用 DefaultNamingTemplate）。
        if (string.IsNullOrWhiteSpace(built))
            built = Build(DefaultNamingTemplate);

        return built;
    }

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
        {
            if (c is ' ' or '\t') sb.Append('_');
            else if (Array.IndexOf(Invalid, c) >= 0) sb.Append('_');
            else sb.Append(c);
        }

        var t = sb.ToString();
        return t.Length > 40 ? t.Substring(0, 40) : t;
    }

    private static void Categorize(OrganizeReport report, RenameLogEntry e)
    {
        report.Results.Add(e);
        if (e.Status.Contains("跳过") || e.Status.Contains("未改动"))
            report.Skipped++;
        else if (e.Status.Contains("错误"))
            report.Failed++;
        else
            report.Processed++;
    }

    private static string OpName(OperationMode m) => m switch
    {
        OperationMode.Copy => "复制",
        OperationMode.Move => "移动",
        _ => "重命名",
    };

    /// <summary>当前指纹算法版本：将来调整纳入字段时递增，即可让旧日志自动失效（旧指纹不再匹配）。</summary>
    private const string FingerprintVersion = "fp2";

    /// <summary>
    /// 上一代指纹算法版本（无条件纳入全部参数）。读侧<b>一并接受</b>它，
    /// 这样算法升级时老用户的历史记录仍能续传，不会每次升级都被迫全量重做一遍。
    /// </summary>
    private const string LegacyFingerprintVersion = "fp1";

    /// <summary>由 AI 产出的占位符：只有模板用到它们时，引擎 / 模型 / 语言才会改变输出文件名。</summary>
    /// <remarks>与 <see cref="ImageAnalysisHelper.BuildPrompt"/> 共用同一份定义，避免两处各写一份导致漂移。</remarks>
    private static readonly string[] AiPlaceholders = ImageAnalysisHelper.AiPlaceholders;

    /// <summary>由拍摄/修改时间产出的占位符：只有模板用到它们时，「取 EXIF 日期」才会改变输出文件名。</summary>
    private static readonly string[] DatePlaceholders =
        { "{yyyy}", "{MM}", "{dd}", "{HH}", "{mm}", "{ss}", "{date}" };

    private static bool TemplateUsesAny(string template, string[] placeholders)
    {
        if (string.IsNullOrEmpty(template)) return false;
        foreach (var p in placeholders)
            if (template.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// 本批次是否「真的会用到 AI 结果」：命名模板含至少一个 AI 占位符，且不是模拟运行。
    /// </summary>
    /// <remarks>
    /// 三个用途共用这一个判据，避免三处各写一份导致漂移：
    /// <list type="bullet">
    /// <item><description><see cref="CreateAi"/>：为 false 时不构造引擎、<b>也不做 Key / 端点 / 模型名校验</b>
    /// （P1-3：模板不用 AI 占位符时，缺 Key 不该把整批拦下）。</description></item>
    /// <item><description><see cref="ProcessOneAsync"/>：为 false 时不发起 AnalyzeAsync
    /// （P1-3 / P1-3b：既不付费空转，也不在模拟运行下真实计费）。</description></item>
    /// <item><description>运行指纹（<see cref="ComputeFingerprint"/> 的 usesAiPlaceholders）
    /// 用的是同一个「模板是否含 AI 占位符」判定，只是<b>不含</b> DryRun 条件
    /// ——模拟运行与实际运行的输出文件名必须一致，否则续传索引会失配。</description></item>
    /// </list>
    /// </remarks>
    private static bool TemplateWillUseAi(OrganizeRequest req)
        => !req.DryRun && TemplateUsesAny(req.NamingTemplate ?? "", AiPlaceholders);

    /// <summary>
    /// 计算本次运行的「参数指纹」：把所有<b>影响输出文件名或目标路径</b>的参数拼成一个字符串。
    /// 用途：写入 rename_log.csv，下次续传时只有指纹相同的记录才被当作「已处理」——
    /// 用户改了命名模板 / 换模式 / 换冲突策略后重跑，对应文件会被重新处理，
    /// 而不是被上一轮的成功记录永久跳过（这正是 P0 缺陷：改模板重跑整批静默不动）。
    /// </summary>
    /// <param name="req">本次请求参数。</param>
    /// <param name="output">本次实际的输出目录（重命名模式下即源目录）。</param>
    /// <param name="conditional">
    /// true = 条件性指纹（当前算法 fp2）：只纳入<b>真正会改变文件名</b>的参数；
    /// false = 上一代写法 fp1（无条件纳入全部参数），仅用于读侧兼容旧日志。
    /// </param>
    /// <param name="isArchive">归档模式：日期子目录由拍摄时间决定，故「取 EXIF 日期」恒为有效维度。</param>
    /// <remarks>
    /// <b>为什么必须是「条件性」的</b>（fp1 → fp2 要解决的问题）：
    /// fp1 无条件把 引擎 / 模型 / 语言 / 日期来源 全部纳入。于是用户只是换了个识别引擎，
    /// 而命名模板里根本没用 {category} 这类 AI 占位符时，文件名<b>一个字都不会变</b>，
    /// 指纹却变了 → 全部历史记录失配 → 整批重做，白白重新调用一遍 AI（费时费钱）。
    /// 治好「静默跳过」的同时不该引入「无谓重做」，所以改为：
    /// <b>模板用到哪类占位符，对应参数才进指纹。</b>
    /// 刻意<b>不</b>纳入：API Key（敏感信息，不落盘）、CustomApiUrl / CustomApiRpmLimit
    /// （端点地址与速率上限不改变「命名结果」本身，修端点不该让整批重做）。
    /// </remarks>
    private static string ComputeFingerprint(OrganizeRequest req, string output, bool conditional, bool isArchive = false)
    {
        var sb = new StringBuilder();
        // 自由文本字段（用户可直接输入，可能含分隔符 '|'）统一走 Field() 加「长度前缀」：
        // 不加长度的话，(模板="a|1", 语言="") 与 (模板="a", 语言="1") 会拼出同一个指纹，
        // 碰撞方向恰好是「参数明明变了却被当成没变 → 续传跳过」，也就是本修复要消灭的那个 bug。
        // 枚举 / 布尔字段不含 '|'，无需前缀。
        void Field(string v) => sb.Append('|').Append(v.Length).Append(':').Append(v);

        string template = req.NamingTemplate ?? "";

        // 条件性纳入的判定：conditional=false 时保持 fp1 的全量口径（读侧兼容用）。
        bool usesAiPlaceholders = !conditional || TemplateUsesAny(template, AiPlaceholders);
        bool usesDatePlaceholders = !conditional || isArchive || TemplateUsesAny(template, DatePlaceholders);

        sb.Append(conditional ? FingerprintVersion : LegacyFingerprintVersion);
        sb.Append('|').Append(req.Mode);                 // 模式决定目标目录与是否原地改名
        sb.Append('|').Append(req.Conflict);             // 冲突策略决定同名文件处理方式
        Field(template);                                 // 命名模板直接影响输出文件名

        if (usesDatePlaceholders)
            sb.Append('|').Append(req.UseExifDate ? 1 : 0);  // 仅当模板真的用到日期占位符（或归档）时才有效

        if (usesAiPlaceholders)
        {
            Field(req.Language ?? "");                   // 提示语言影响 AI 产出的命名内容
            sb.Append('|').Append(req.AiProvider);       // 引擎不同 → 命名结果不同
            Field(req.CustomApiModel ?? "");             // 自定义模型名：不同模型命名风格不同
        }

        Field(output);                                   // 输出目录：换目录就该往新目录重做
        return sb.ToString();
    }

    /// <summary>
    /// 构造本批次的 AI 引擎实例。
    /// <b>已改为实例方法</b>（原为 static）：需要把 <see cref="DelayBackoffHonoringPauseAsync"/>
    /// 作为「退避等待替换钩子」注入各引擎的构造函数——该钩子要读实例字段 <c>_pts</c>（暂停令牌），
    /// 静态方法拿不到。注意构造发生在 <c>_pts</c> 创建之前（见 <c>RunAsync</c>），但委托里是
    /// <b>调用时</b>才读 <c>_pts</c>，处理循环开始时它必定已就绪。
    /// </summary>
    private IImageAnalysisService? CreateAi(OrganizeRequest req)
    {
        if (req.AiProvider == AiProvider.None) return null;

        // P1-3 / P1-3b：本批次根本用不到 AI 结果时不创建引擎，也<b>不做</b>下面的 Key / 端点 / 模型名校验。
        // 判断口径见 <see cref="TemplateWillUseAi"/>。
        // ① 命名模板不含任何 AI 占位符时，BuildName 不会把 AI 字段写进文件名（见 AiPlaceholders），
        //    调用纯属付费空转；此前还会因为 Key 未配置而把「根本不需要 AI」的整批直接拦下。
        //    这与「条件性指纹」是同一条不变量：模板不用 AI 占位符 ⇒ 引擎 / 模型 / 语言都不影响输出。
        // ② 模拟运行（DryRun）承诺「不改动任何文件，仅预览结果」，不应产生真实计费请求。
        if (!TemplateWillUseAi(req)) return null;

        // 以下三处「配置缺失」与 AI 层（ImageAnalysisHelper 的端点 / Key / 模型名 / https 校验）
        // 同口径：空配置对每个文件都会以完全相同的方式失败，重试没有任何成功可能，
        // 故统一为整批级永久错误。isBatchLevel: true 必须显式写出（P26：不可依赖默认值）。
        // 注意：这三处都在<b>扫描之前</b>抛出，不进逐文件循环、不进重排队与分档，
        // 由 ViewModel 的通用 catch 显示提示——换的是类型，行为与改造前一致；
        // 换类型的目的是让「配置缺失 ⟹ AiPermanentException」这条不变量全域成立，
        // 将来 VM 若按 PermanentOperationException 做定向引导（如「去设置页」按钮）不会漏掉这里。
        if (string.IsNullOrWhiteSpace(req.AiApiKey))
            throw new AiPermanentException(
                $"已选择识别引擎「{req.AiProvider}」但未配置 API Key。请打开「设置」填写对应 Key 后再开始整理。",
                isBatchLevel: true);

        // 退避等待钩子：把 AI 层内部的 Task.Delay 换成本方法，让「暂停」能打断 AI 退避
        // （否则退避期间点暂停要等整段 AI 调用结束才生效，最长约 4 分钟，期间界面已显示
        //  「已暂停」却仍在发请求计费 —— P33 谎报）。四个引擎的构造函数都已支持该参数。
        Func<TimeSpan, CancellationToken, Task> delayAsync = DelayBackoffHonoringPauseAsync;

        if (req.AiProvider == AiProvider.Custom)
        {
            if (string.IsNullOrWhiteSpace(req.CustomApiUrl))
                throw new AiPermanentException("自定义引擎未配置端点 URL：请打开「设置」填写自定义 API 端点。",
                    isBatchLevel: true);
            if (string.IsNullOrWhiteSpace(req.CustomApiModel))
                throw new AiPermanentException("自定义引擎未配置模型名：请打开「设置」填写自定义模型名。",
                    isBatchLevel: true);
            // rpmLimit 为 0 时保持端点嗅探得出的闸门（不替用户猜 RPM）
            return new CustomImageAnalysisService(req.CustomApiUrl, req.CustomApiModel, req.AiApiKey, req.CustomApiRpmLimit,
                req.NamingTemplate, delayAsync);
        }

        return req.AiProvider switch
        {
            // 传入命名模板：提示词只要求模型输出模板真正用到的字段（省 token，也让模型更专注）。
            AiProvider.Zhipu => new ZhipuImageAnalysisService(req.AiApiKey, req.NamingTemplate, delayAsync),
            AiProvider.Qwen => new QwenImageAnalysisService(req.AiApiKey, req.NamingTemplate, delayAsync),
            AiProvider.Nvidia => new NvidiaImageAnalysisService(req.AiApiKey, req.NamingTemplate, delayAsync),
            _ => null,
        };
    }

    /// <summary>
    /// 退避等待期间的暂停轮询粒度：250ms 兼顾「暂停响应足够及时」与「不空转 CPU」。
    /// 最坏一次退避 120s + 抖动 ≈ 480 次唤醒，代价可忽略。
    /// </summary>
    private static readonly TimeSpan PausePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// AI 层「退避等待」的替换实现（契约见 <see cref="ImageAnalysisHelper.CallVisionApiAsync"/> 的
    /// <c>delayAsync</c>）：把 <c>Task.Delay</c> 换成「可被暂停打断」的等待。
    /// <para><b>为什么是「分段轮询」而不是
    /// <c>Task.WhenAny(Task.Delay(d, ct), _pts.WaitWhilePausedAsync(ct))</c>：</b>
    /// <see cref="PauseTokenSource.WaitWhilePausedAsync"/> 的语义是「<b>若</b>当前已暂停则挂起」，
    /// <b>不是</b>「等到被暂停为止」——未暂停时它返回的是一个<b>已完成的</b> Task，
    /// 于是 WhenAny 会立刻返回、整段退避被跳过，指数退避直接失效（退化为全速重试、疯狂撞限流）。
    /// 故这里改为每 <see cref="PausePollInterval"/> 检查一次暂停状态：暂停期间不推进剩余时长，
    /// 恢复后继续把剩余退避等满。</para>
    /// <para><b>与 AI 层计数 / 预算的关系：</b>暂停不会产生新的重试循环迭代，故<b>不递增</b> AI 层的
    /// attempt 计数；但 AI 层的 180s 退避预算是墙钟 <c>Stopwatch</c>，暂停时长会被计入
    /// （本侧无法扣减，也不偷偷改 AI 层语义）——影响见交付说明。</para>
    /// <para>契约与 <see cref="Task.Delay(TimeSpan, CancellationToken)"/> 一致：等满时长后返回，
    /// <paramref name="ct"/> 取消时抛 <see cref="OperationCanceledException"/>。</para>
    /// </summary>
    private async Task DelayBackoffHonoringPauseAsync(TimeSpan delay, CancellationToken ct)
    {
        var pts = _pts;
        if (pts == null)
        {
            // 未创建暂停令牌（理论上处理循环内不会发生）：退回原生等待，行为与改造前一致
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return;
        }

        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            if (pts.IsPaused)
            {
                await pts.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                // 必须在这里显式检查取消：WaitWhilePausedAsync 被 ct 唤醒时是「正常返回」而不是抛异常，
                // 而此刻 _paused 仍为 true、waiter 仍是那个已完成的 TCS —— 直接 continue 会在下一轮
                // 立刻再次返回，形成 100% CPU 的空转死循环（暂停态下点取消就会命中）。
                ct.ThrowIfCancellationRequested();
                continue; // 暂停不计入退避时长：恢复后重新等满剩余部分
            }

            var slice = remaining < PausePollInterval ? remaining : PausePollInterval;
            var sliceSw = Stopwatch.StartNew();
            await Task.Delay(slice, ct).ConfigureAwait(false);
            remaining -= sliceSw.Elapsed;
        }
    }
}

/// <summary>
/// 协作式暂停令牌源：调用 <see cref="Pause"/> 后，等待该令牌的异步操作会挂起，直到
/// <see cref="Resume"/>（或传入的 <see cref="CancellationToken"/> 触发取消）才继续。
/// 与 <see cref="CancellationToken"/> 的「硬取消」区分：暂停不丢弃已扫描的工作队列与状态，可随时继续。
/// </summary>
internal sealed class PauseTokenSource
{
    private readonly object _gate = new();

    private TaskCompletionSource<bool>? _waiter;

    private bool _paused;

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult(true);
    }

    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            if (!_paused) return;
            waiter = _waiter;
        }

        if (waiter == null) return;

        // P2-10：注册必须释放，否则暂停期间每个文件都会残留一个回调直到 Token 结束。
        // 取消时唤醒等待，使循环顶部的 ThrowIfCancellationRequested 能抛出，避免暂停态下取消死锁。
        CancellationTokenRegistration reg = default;
        if (ct.CanBeCanceled)
        {
            reg = ct.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), waiter);
        }

        try
        {
            await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            // P1-1 复核修正：async 上下文中正确 await ValueTask 释放注册
            await reg.DisposeAsync().ConfigureAwait(false);
        }
    }
}
