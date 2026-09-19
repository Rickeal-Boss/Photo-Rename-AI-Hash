using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        IImageAnalysisService? ai = CreateAi(req);

        // 暂停令牌必须在扫描之前创建：此前它在处理循环前才 new，而 Pause() 在 _pts == null 时是
        // 静默 no-op，导致「开始后的扫描 / 加载索引窗口内点暂停」完全失效、UI 却谎报已暂停。
        // 上移后这两个窗口内点暂停即可生效：扫描本身不检查暂停，扫描一结束、处理任何文件之前就挂起。
        _pts = new PauseTokenSource();
        ApplyPendingPause(); // 补应用「令牌创建之前」（校验 / 构造 AI 阶段）点下的暂停
        var files = await _photo.ScanAsync(req.SourceFolder, ct).ConfigureAwait(false);
        report.Total = files.Count;

        // 断点续传：读取输出目录（含递归子文件夹）的重命名日志，跳过「已按目标格式命名完成」的文件，
        // 避免重复处理（例如已正确命名的 game_古建筑竞技场_..._screenshot.png）。
        var completed = await _log.LoadRenameLogAsync(output).ConfigureAwait(false);

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
            // 目标文件名匹配仅在「重命名」模式下作为辅助（重命名后文件名即新名，避免重复处理）。
            // Copy/Move 模式不依赖 DoneByName，避免新加入且恰与旧目标同名的文件被误跳。
            bool doneBySource = completed.DoneBySource.Contains(f.Path);
            bool doneByName = req.Mode == OperationMode.Rename && completed.DoneByName.Contains(curName);
            if (doneBySource || doneByName)
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

        var attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const int maxPerFileAttempts = 10; // 单文件最大尝试次数：兜底防止个别图片永久卡死循环

        // 本批次已实际落地的目标路径：ResolveTargetAsync 据此拒绝 Overwrite 覆盖本批次自己产出的文件。
        // 只按批次存活、不做实例字段：本类是单例，实例字段会在并发批次之间互相污染。
        var claimedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 连续命中「不可恢复的识别错误」的文件数。达到阈值说明该问题（账户欠费 / 额度耗尽 /
        // 模型不存在 / 参数非法）对整批都成立，继续只会产生 N 次必然失败的请求与 N 行同因错误。
        int consecutivePermanent = 0;

        // A-01：重试会把同一文件重新入队，但重试只应针对「AI 之后」的失败（目标被占用、
        // 备份失败、路径异常等）。缓存成功结果，避免单文件最坏 10 次重复计费与请求放大；
        // AI 自身失败（异常）不入缓存，重试仍会重新请求（瞬时故障需要重试）。
        var aiCache = new Dictionary<string, ImageAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var md5Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    var entry = await ProcessOneAsync(f, req, output, ai, index, ct, aiCache, md5Cache, claimedThisRun).ConfigureAwait(false);
                    Categorize(report, entry);
                    consecutivePermanent = 0; // 成功处理即重置：仅「连续」失败才熔断，容忍偶发假阳性
                    done++;
                    progress.Report(new OrganizeProgress
                    {
                        Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                        Result = entry,
                        LogLine = $"{entry.OriginalName} -> {entry.NewName} [{entry.Status}]",
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

                    // 仅「对整批成立」的永久错误才累计熔断：
                    // 逐文件永久错误（如单个文件无法解码）按既定口径只记该文件的错误、继续处理其它文件。
                    if (ex.IsBatchLevel)
                    {
                        // 非环境的永久错误（AI 账户/额度/模型配置类）按既定口径仍记单文件失败，
                        // 但若连续多个文件都命中，说明是整批级根因：提前中止，避免刷满 N 行同因错误。
                        consecutivePermanent++;
                        if (consecutivePermanent >= 3)
                        {
                            // 不改变单次重试（15×15s）与单文件重排队（10 次）的既定口径，
                            // 只把「整批继续」的终止时机提前。
                            progress.Report(new OrganizeProgress
                            {
                                Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                                LogLine = $"已中止：连续 {consecutivePermanent} 个文件命中不可恢复的识别错误（{ex.Message}）" +
                                          $"（此前已处理 {done} 个，共 {files.Count} 个）。请处理账户/额度或模型配置后重跑。",
                            });
                            throw;
                        }
                    }
                    // 逐文件级（IsBatchLevel == false）有意「既不累加也不归零」：
                    // 不累加 → 单个坏文件（如无法解码）不会触发熔断；
                    // 不归零 → 也不掩盖此前已累积的整批级证据（4xx → 4xx → 解码失败 → 4xx 仍会中止）。
                    // 归零只发生在上面 try 分支里「文件被成功处理」时。
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException) throw; // 取消立即向上传播，不进入重试逻辑
                    attempts.TryGetValue(f.Path, out int n);
                    n++;
                    if (n < maxPerFileAttempts)
                    {
                        // 重新排队到队尾，稍后再次尝试（保留原序号，避免重命名序号错乱）
                        attempts[f.Path] = n;
                        queue.Enqueue((f, index));
                        progress.Report(new OrganizeProgress
                        {
                            // 重试不算完成（done 未递增），沿用当前百分比即可：不给值会被 VM 打回 0%
                            Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                            LogLine = $"{f.Name} [重试 {n}/{maxPerFileAttempts}] 上次失败：{ex.Message}",
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
                            Message = ex.Message,
                        };
                        report.Results.Add(err);
                        progress.Report(new OrganizeProgress
                        {
                            Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                            Result = err,
                            LogLine = $"{f.Name} [错误] {ex.Message}",
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

        // 本批次已实际落地的目标路径（语义同 RunAsync）
        var claimedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 同 RunAsync：暂停令牌必须在扫描之前创建，否则扫描 / 加载索引窗口内点暂停是静默 no-op。
        _pts = new PauseTokenSource();
        ApplyPendingPause(); // 同 RunAsync：补应用令牌创建之前点下的暂停
        var files = await _photo.ScanAsync(req.SourceFolder, ct).ConfigureAwait(false);
        report.Total = files.Count;

        // 断点续传：读取输出目录（含递归子文件夹）的重命名日志，跳过已归档完成（源路径已记录）的文件。
        var completed = await _log.LoadRenameLogAsync(req.OutputFolder).ConfigureAwait(false);
        try
        {
            int done = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                await _pts.WaitWhilePausedAsync(ct).ConfigureAwait(false); // 协作式暂停
                // 续传跳过：源路径精确匹配优先；目标文件名匹配仅限重命名模式（避免 Copy/Move 误跳新文件）。
                bool doneBySource = completed.DoneBySource.Contains(f.Path);
                bool doneByName = req.Mode == OperationMode.Rename && completed.DoneByName.Contains(f.Name);
                if (doneBySource || doneByName)
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
                    string md5 = await _hash.TryComputeMd5Async(f.Path, ct).ConfigureAwait(false) ?? "";
                    DateTime when = f.LastModified;
                    if (req.UseExifDate)
                    {
                        var ex = _photo.GetDateTaken(f.Path);
                        if (ex.HasValue) when = ex.Value;
                    }

                    string destDir = Path.Combine(req.OutputFolder, when.ToString("yyyy"), when.ToString("yyyy-MM-dd"));
                    if (!req.DryRun) Directory.CreateDirectory(destDir);

                    var (resolved, targetMd5, degraded) = await ResolveTargetAsync(destDir, f.Name, md5, req.Conflict, claimedThisRun, ct).ConfigureAwait(false);
                    if (resolved == null)
                    {
                        entry = new RenameLogEntry
                        {
                            OriginalPath = f.Path,
                            OriginalName = f.Name,
                            Md5 = md5,
                            Operation = "归档",
                            Status = "跳过(已存在)",
                        };
                    }
                    else
                    {
                        string status = await ExecuteAsync(req, f.Path, resolved, targetMd5, md5, "归档", ct).ConfigureAwait(false);

                        // 与 RunAsync 同口径：登记本批次已落地的目标；模拟运行不登记。
                        if (!req.DryRun) claimedThisRun.Add(resolved);

                        entry = new RenameLogEntry
                        {
                            OriginalPath = f.Path,
                            OriginalName = f.Name,
                            NewPath = resolved,
                            NewName = Path.GetFileName(resolved),
                            Md5 = md5,
                            Operation = "归档",
                            Status = status,
                        };
                        if (degraded)
                        {
                            entry.Message = $"目标已被本批次其他文件占用，已自动重命名为 {Path.GetFileName(resolved)}" +
                                            "（Overwrite 不会覆盖本批次已产出的文件，避免静默丢数据）";
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
                    if (ex is OperationCanceledException) throw; // 与 RunAsync 对齐：取消立即向上传播，不记成「错误」
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

    private async Task<RenameLogEntry> ProcessOneAsync(
        PhotoFile f, OrganizeRequest req, string output, IImageAnalysisService? ai, int index, CancellationToken ct,
        Dictionary<string, ImageAnalysisResult> aiCache, Dictionary<string, string> md5Cache,
        HashSet<string> claimedThisRun)
    {
        // A-01：MD5 结果缓存——重试时复用，避免对同一文件重复计算（大图 MD5 为全文件读取）。
        if (!md5Cache.TryGetValue(f.Path, out var md5))
        {
            md5 = await _hash.TryComputeMd5Async(f.Path, ct).ConfigureAwait(false) ?? "";
            // 仅在成功算出 MD5 时入缓存：缓存空串会让重试走到「内容不同」分支，
            // 在 AutoRename 策略下生成 name_1 / name_2 重复副本。
            if (md5.Length > 0) md5Cache[f.Path] = md5;
        }

        // 拍摄时间
        DateTime when = f.LastModified;
        if (req.UseExifDate)
        {
            var ex = _photo.GetDateTaken(f.Path);
            if (ex.HasValue) when = ex.Value;
        }

        // AI 识别（结果缓存：重试复用，避免重复计费）
        if (ai != null)
        {
            if (!aiCache.TryGetValue(f.Path, out var res))
            {
                res = await ai.AnalyzeAsync(f.Path, req.Language, ct).ConfigureAwait(false);
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

        // 生成新名：未配置 AI 引擎时回退到「日期+原名+序号」，避免 unknown_…_unknown 垃圾名
        // 模板为空（用户清空「命名规则」输入框，或 settings.json 中该字段为 null）时回退到默认模板：
        // 否则 BuildName 返回空串，最终文件名只剩扩展名（如 ".jpg"），批量文件还会互相撞名。
        string template = ai != null && !string.IsNullOrWhiteSpace(req.NamingTemplate)
            ? req.NamingTemplate
            : "{yyyy}{MM}{dd}_{name}_{n}";
        string baseName = BuildName(f, template, when, index);
        string candidate = baseName + Path.GetExtension(f.Name);

        // 目标冲突检测（P2-7：一并取回目标 MD5，ExecuteAsync 直接复用，避免重复计算）
        var (resolved, targetMd5, degraded) = await ResolveTargetAsync(output, candidate, md5, req.Conflict, claimedThisRun, ct).ConfigureAwait(false);
        if (resolved == null)
        {
            var skip = new RenameLogEntry
            {
                OriginalPath = f.Path,
                OriginalName = f.Name,
                Md5 = md5,
                Operation = OpName(req.Mode),
                Status = "跳过(已存在)",
            };
            if (!req.DryRun) await _log.AppendRenameLogAsync(output, skip).ConfigureAwait(false);
            return skip;
        }

        // 重命名模式实际执行前，先把原文件备份到用户指定的备份文件夹（保险）
        if (req.Mode == OperationMode.Rename && !req.DryRun &&
            !string.IsNullOrWhiteSpace(req.BackupFolder) &&
            !string.Equals(resolved, f.Path, StringComparison.OrdinalIgnoreCase))
        {
            await BackupOriginalAsync(req.BackupFolder, f.Path, ct).ConfigureAwait(false);
        }

        string status = await ExecuteAsync(req, f.Path, resolved, targetMd5, md5, OpName(req.Mode), ct).ConfigureAwait(false);

        // 登记本批次已落地的目标：Overwrite 不得再覆盖它（判定见 ResolveTargetAsync）。
        // 模拟运行不登记：没有真实落地，登记会让模拟结果互相「占位」，与实跑不一致。
        if (!req.DryRun) claimedThisRun.Add(resolved);

        var entry = new RenameLogEntry
        {
            OriginalPath = f.Path,
            OriginalName = f.Name,
            NewPath = resolved,
            NewName = Path.GetFileName(resolved),
            Md5 = md5,
            Operation = OpName(req.Mode),
            Status = status,
        };
        // 退化提示：Overwrite 撞上本批次自己刚写的文件时会退化为自动重命名，写明原因，
        // 避免用户疑惑「为什么多了一个 _1 后缀」，也表明这不是失败。
        if (degraded)
        {
            entry.Message = $"目标已被本批次其他文件占用，已自动重命名为 {Path.GetFileName(resolved)}" +
                            "（Overwrite 不会覆盖本批次已产出的文件，避免静默丢数据）";
        }
        if (!req.DryRun) await _log.AppendRenameLogAsync(output, entry).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// 执行实际的 copy/move/rename。
    /// - 若目标已存在且内容相同(MD5)，视为已存在、不重复写入；
    /// - Overwrite 模式直接覆盖；其余模式目标已处理为唯一名。
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

        bool overwrite = req.Conflict == ConflictStrategy.Overwrite;
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

    /// <summary>备份文件名去重的最大尝试次数（原名 + _1…_9998），与 <see cref="SuffixUntilFreeAsync"/> 的 9999 上限一致。</summary>
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

        for (int i = 1; i <= MaxBackupNameAttempts; i++)
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
            $"备份目录中没有可用文件名（{Path.GetFileName(source)} 及其 _1…_{MaxBackupNameAttempts - 1} 后缀均已被占用）。");
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
    /// 计算最终写入目标路径：
    /// - 不存在 → 直接返回；
    /// - 存在且内容相同(MD5) → 返回该路径（调用方按「未改动」处理）；
    /// - 存在且内容不同 → 按策略：Skip 返回 null（跳过），Overwrite 返回该路径，AutoRename 追加 _1/_2 直到唯一（同样以 MD5 判定是否重复内容）。
    /// P2-7：目标已存在时一并返回其 MD5，供 ExecuteAsync 复用，避免每文件重复计算 MD5。
    /// <paramref name="claimedThisRun"/>：本批次已实际落地的目标路径。Overwrite 撞上其中的路径时
    /// 退化为自动重命名（Degraded=true）——覆盖它会静默销毁本批次已产出的成果，Move 模式等于丢数据。
    /// <returns>Degraded：仅当「本应选 Overwrite 但因撞上本批次产出而改为加序号」时为 true。</returns>
    /// </summary>
    private async Task<(string? Target, string? TargetMd5, bool Degraded)> ResolveTargetAsync(
        string output, string candidate, string md5, ConflictStrategy conflict,
        HashSet<string> claimedThisRun, CancellationToken ct)
    {
        string target = Path.Combine(output, candidate);
        if (!File.Exists(target)) return (target, null, false);

        string existing = await _hash.TryComputeMd5Async(target, ct).ConfigureAwait(false) ?? "";
        if (!string.IsNullOrEmpty(md5) && md5 == existing)
            return (target, existing, false); // 内容相同，无需动作

        // 原写法是 switch 表达式，加入「Overwrite 撞本批次产出」的条件分支后需带 when 子句，
        // 故改为 if/else 语句形式，语义等价且分支更直观（Skip / Overwrite / 其余=AutoRename 全覆盖）。
        if (conflict == ConflictStrategy.Skip) return (null, null, false);

        if (conflict == ConflictStrategy.Overwrite && !claimedThisRun.Contains(target))
            return (target, existing, false);

        // 走到这里有两种情况：AutoRename，或 Overwrite 但 target 是本批次自己刚写进去的文件。
        // 后者必须退化为自动重命名：覆盖它会静默销毁本批次已产出的成果，Move 模式尤其致命——
        // 源文件已移出源目录且不做备份，被覆盖的内容没有任何副本，等于直接丢数据。
        var (suffixed, suffixMd5) = await SuffixUntilFreeAsync(output, candidate, md5, ct).ConfigureAwait(false);
        return (suffixed, suffixMd5, conflict == ConflictStrategy.Overwrite);
    }

    /// <summary>P2-7：返回最终空位目标路径；窗口内 MD5 命中相同内容时一并传出该目标 MD5。</summary>
    private async Task<(string Target, string? TargetMd5)> SuffixUntilFreeAsync(string output, string candidate, string md5, CancellationToken ct)
    {
        string name = Path.GetFileNameWithoutExtension(candidate);
        string ext = Path.GetExtension(candidate);
        string target = Path.Combine(output, candidate);
        int i = 1;
        while (File.Exists(target))
        {
            string em = await _hash.TryComputeMd5Async(target, ct).ConfigureAwait(false) ?? "";
            if (!string.IsNullOrEmpty(md5) && md5 == em)
                return (target, em); // 该序号名下已是相同内容
            target = Path.Combine(output, $"{name}_{i}{ext}");
            if (++i > 9999) break;
        }

        return (target, null);
    }

    /// <summary>最终文件基名长度上限：单字段 40 字符 × 6 字段模板 + 分隔符 + 扩展名 + 目录深度
    /// 可能超过 MAX_PATH（app.manifest 已按既定取舍移除 longPathAware），此处整体收口。</summary>
    private const int MaxBaseNameLength = 180;

    private static string BuildName(PhotoFile f, string template, DateTime when, int index)
    {
        string San(string v) => Sanitize(v);
        string Ai(string v) => string.IsNullOrWhiteSpace(v) ? "unknown" : San(v);

        var built = (template ?? "")
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

        // A-06：对最终基名整体截断，避免多字段模板叠加目录深度后触发 PathTooLongException
        if (built.Length > MaxBaseNameLength)
            built = built.Substring(0, MaxBaseNameLength).TrimEnd('_', ' ', '.');

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

    private static IImageAnalysisService? CreateAi(OrganizeRequest req)
    {
        if (req.AiProvider == AiProvider.None) return null;

        if (string.IsNullOrWhiteSpace(req.AiApiKey))
            throw new InvalidOperationException(
                $"已选择识别引擎「{req.AiProvider}」但未配置 API Key。请打开「设置」填写对应 Key 后再开始整理。");

        if (req.AiProvider == AiProvider.Custom)
        {
            if (string.IsNullOrWhiteSpace(req.CustomApiUrl))
                throw new InvalidOperationException("自定义引擎未配置端点 URL：请打开「设置」填写自定义 API 端点。");
            if (string.IsNullOrWhiteSpace(req.CustomApiModel))
                throw new InvalidOperationException("自定义引擎未配置模型名：请打开「设置」填写自定义模型名。");
            // rpmLimit 为 0 时保持端点嗅探得出的闸门（不替用户猜 RPM）
            return new CustomImageAnalysisService(req.CustomApiUrl, req.CustomApiModel, req.AiApiKey, req.CustomApiRpmLimit);
        }

        return req.AiProvider switch
        {
            AiProvider.Zhipu => new ZhipuImageAnalysisService(req.AiApiKey),
            AiProvider.Qwen => new QwenImageAnalysisService(req.AiApiKey),
            AiProvider.Nvidia => new NvidiaImageAnalysisService(req.AiApiKey),
            _ => null,
        };
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
