using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;
using Windows.Storage;

namespace PhotoRenameAIHash.Services;

/// <summary>
/// 整理/归档核心流程：扫描 →（AI 识别命名）→ 感知哈希去重 → 生成新名 →
/// 目标冲突检测（MD5 相同则跳过，否则按策略加 _1/_2 序号）→ 执行 copy/move/rename
/// → 写重命名日志。全程保证批量操作下文件名的唯一性与可追溯性。
/// </summary>
public sealed class OrganizeService : IOrganizeService
{
    private readonly IPhotoService _photo = AppServices.PhotoService;
    private readonly IHashService _hash = AppServices.HashService;
    private readonly RenameLogService _log = AppServices.RenameLogService;

    private PauseTokenSource? _pts;

    /// <summary>当前是否处于暂停状态（供 UI 显示「继续」）。</summary>
    public bool IsPaused => _pts?.IsPaused ?? false;

    /// <summary>协作式暂停：挂起处理循环，保留已扫描的工作队列与状态，可随时继续（区别于 Cancel 的硬取消）。</summary>
    public void Pause() => _pts?.Pause();

    /// <summary>继续被暂停的处理循环。</summary>
    public void Resume() => _pts?.Resume();

    public async Task<OrganizeReport> RunAsync(OrganizeRequest req, IProgress<OrganizeProgress> progress, CancellationToken ct = default)
    {
        var report = new OrganizeReport();
        string output = req.Mode == OperationMode.Rename ? req.SourceFolder : req.OutputFolder;

        if (string.IsNullOrWhiteSpace(req.SourceFolder) || !Directory.Exists(req.SourceFolder))
        {
            progress.Report(new OrganizeProgress { Message = "源文件夹无效。" });
            return report;
        }

        if (req.Mode != OperationMode.Rename &&
            (string.IsNullOrWhiteSpace(output) || !Directory.Exists(output)))
        {
            progress.Report(new OrganizeProgress { Message = "输出文件夹无效。" });
            return report;
        }

        if (req.EnableDedup) await _log.LoadDedupLibraryAsync().ConfigureAwait(false);

        IImageAnalysisService? ai = CreateAi(req);
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
            // 续传跳过：当前文件名已是某条成功记录的目标名，或该源路径此前已成功处理。
            if (completed.DoneByName.Contains(curName) || completed.DoneBySource.Contains(f.Path))
            {
                skippedAtStart++;
                report.Skipped++;
                progress.Report(new OrganizeProgress { LogLine = $"{curName} 跳过(日志已完成)" });
                continue;
            }
            queue.Enqueue((f, order));
        }

        var attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const int maxPerFileAttempts = 10; // 单文件最大尝试次数：兜底防止个别图片永久卡死循环
        int done = skippedAtStart; // 已跳过的也算进度推进

        _pts = new PauseTokenSource();
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            await _pts.WaitWhilePausedAsync(ct).ConfigureAwait(false); // 协作式暂停：挂起直到继续或取消
            var (f, index) = queue.Dequeue();
            try
            {
                var entry = await ProcessOneAsync(f, req, output, ai, index, ct).ConfigureAwait(false);
                Categorize(report, entry);
                done++;
                progress.Report(new OrganizeProgress
                {
                    Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                    Result = entry,
                    LogLine = $"{entry.OriginalName} -> {entry.NewName} [{entry.Status}]",
                });
            }
            catch (Exception ex)
            {
                attempts.TryGetValue(f.Path, out int n);
                n++;
                if (n < maxPerFileAttempts)
                {
                    // 重新排队到队尾，稍后再次尝试（保留原序号，避免重命名序号错乱）
                    attempts[f.Path] = n;
                    queue.Enqueue((f, index));
                    progress.Report(new OrganizeProgress
                    {
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

        if (req.EnableDedup) await _log.SaveDedupLibraryAsync().ConfigureAwait(false);
        _pts = null;
        progress.Report(new OrganizeProgress
        {
            Message = $"完成：处理 {report.Processed}，跳过 {report.Skipped}（含续传跳过 {skippedAtStart}），失败 {report.Failed}。",
        });
        return report;
    }

    public async Task<OrganizeReport> ArchiveByDateAsync(OrganizeRequest req, IProgress<OrganizeProgress> progress, CancellationToken ct = default)
    {
        var report = new OrganizeReport();

        if (string.IsNullOrWhiteSpace(req.SourceFolder) || !Directory.Exists(req.SourceFolder))
        {
            progress.Report(new OrganizeProgress { Message = "源文件夹无效。" });
            return report;
        }

        if (string.IsNullOrWhiteSpace(req.OutputFolder) || !Directory.Exists(req.OutputFolder))
        {
            progress.Report(new OrganizeProgress { Message = "输出文件夹无效。" });
            return report;
        }

        if (req.EnableDedup) await _log.LoadDedupLibraryAsync().ConfigureAwait(false);

        var files = await _photo.ScanAsync(req.SourceFolder, ct).ConfigureAwait(false);
        report.Total = files.Count;

        // 断点续传：读取输出目录（含递归子文件夹）的重命名日志，跳过已归档完成（源路径已记录）的文件。
        var completed = await _log.LoadRenameLogAsync(req.OutputFolder).ConfigureAwait(false);
        _pts = new PauseTokenSource();

        int done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            await _pts.WaitWhilePausedAsync(ct).ConfigureAwait(false); // 协作式暂停
            // 续传跳过：当前文件名已是某条成功记录的目标名，或该源路径此前已成功处理。
            if (completed.DoneByName.Contains(f.Name) || completed.DoneBySource.Contains(f.Path))
            {
                report.Skipped++;
                done++;
                progress.Report(new OrganizeProgress { LogLine = $"{f.Name} 跳过(日志已完成)" });
                continue;
            }
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

                string? resolved = await ResolveTargetAsync(destDir, f.Name, md5, req.Conflict, ct).ConfigureAwait(false);
                RenameLogEntry entry;
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
                    string status = await ExecuteAsync(req, f.Path, resolved, md5, "归档", ct).ConfigureAwait(false);
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
                    if (!req.DryRun) await _log.AppendRenameLogAsync(destDir, entry).ConfigureAwait(false);
                }

                Categorize(report, entry);
            }
            catch (Exception ex)
            {
                report.Failed++;
                report.Results.Add(new RenameLogEntry
                {
                    OriginalName = f.Name,
                    Status = "错误",
                    Message = ex.Message,
                });
            }

            done++;
            progress.Report(new OrganizeProgress
            {
                Percent = (int)(100.0 * done / Math.Max(1, files.Count)),
                LogLine = $"{f.Name} 已归档进度 {done}/{files.Count}",
            });
        }

        if (req.EnableDedup) await _log.SaveDedupLibraryAsync().ConfigureAwait(false);
        _pts = null;
        progress.Report(new OrganizeProgress
        {
            Message = $"归档完成：处理 {report.Processed}，跳过 {report.Skipped}，失败 {report.Failed}。",
        });
        return report;
    }

    private async Task<RenameLogEntry> ProcessOneAsync(
        PhotoFile f, OrganizeRequest req, string output, IImageAnalysisService? ai, int index, CancellationToken ct)
    {
        string md5 = await _hash.TryComputeMd5Async(f.Path, ct).ConfigureAwait(false) ?? "";
        ulong aHash = req.EnableDedup ? (await _hash.TryComputeAHashAsync(f.Path, 8, ct).ConfigureAwait(false) ?? 0) : 0;
        ulong dHash = req.EnableDedup ? (await _hash.TryComputeDHashAsync(f.Path, 8, ct).ConfigureAwait(false) ?? 0) : 0;

        // 感知哈希去重
        if (req.EnableDedup && _log.IsDuplicateAndRecord(md5, aHash, dHash, f.Path, req.AHashThreshold, req.DHashThreshold, _hash))
        {
            var skip = new RenameLogEntry
            {
                OriginalPath = f.Path,
                OriginalName = f.Name,
                Md5 = md5,
                Operation = OpName(req.Mode),
                Status = "已去重(感知哈希)",
            };
            if (!req.DryRun) await _log.AppendRenameLogAsync(output, skip).ConfigureAwait(false);
            return skip;
        }

        // 拍摄时间
        DateTime when = f.LastModified;
        if (req.UseExifDate)
        {
            var ex = _photo.GetDateTaken(f.Path);
            if (ex.HasValue) when = ex.Value;
        }

        // AI 识别
        if (ai != null)
        {
            var res = await ai.AnalyzeAsync(f.Path, req.Language, ct).ConfigureAwait(false);
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

        // 生成新名
        string baseName = BuildName(f, req.NamingTemplate, when, index);
        string candidate = baseName + Path.GetExtension(f.Name);

        // 目标冲突检测
        string? resolved = await ResolveTargetAsync(output, candidate, md5, req.Conflict, ct).ConfigureAwait(false);
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

        string status = await ExecuteAsync(req, f.Path, resolved, md5, OpName(req.Mode), ct).ConfigureAwait(false);
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
        if (!req.DryRun) await _log.AppendRenameLogAsync(output, entry).ConfigureAwait(false);
        return entry;
    }

    /// <summary>
    /// 执行实际的 copy/move/rename。
    /// - 若目标已存在且内容相同(MD5)，视为已存在、不重复写入；
    /// - Overwrite 模式直接覆盖；其余模式目标已处理为唯一名。
    /// </summary>
    private async Task<string> ExecuteAsync(OrganizeRequest req, string source, string target, string md5, string opName, CancellationToken ct)
    {
        if (File.Exists(target) && !string.IsNullOrEmpty(md5))
        {
            string existing = await _hash.TryComputeMd5Async(target, ct).ConfigureAwait(false) ?? "";
            if (existing == md5)
                return "未改动(内容相同)";
        }

        if (req.DryRun)
            return "模拟(" + opName + ")";

        bool overwrite = req.Conflict == ConflictStrategy.Overwrite;
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

    /// <summary>
    /// 重命名模式实际执行前的保险：把原文件复制一份到备份文件夹。
    /// 采用 MSIX 容器下唯一可靠的写法——通过 FolderPicker 选择路径取得的 StorageFolder/StorageFile
    /// （携带 Broker 令牌）做 CopyAsync，忽略同名冲突（自动加序号），保证无论是否声明
    /// broadFileSystemAccess 都能真正落盘到用户选择的备份文件夹；备份失败直接抛异常，
    /// 由 <see cref="RunAsync"/> 的逐文件 try/catch 标记为「错误」且不执行 rename，避免丢失原文件。
    /// </summary>
    private static async Task BackupOriginalAsync(string backupDir, string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var backupFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(backupDir);
        var srcFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(source);
        await srcFile.CopyAsync(
            backupFolder,
            Path.GetFileName(source),
            Windows.Storage.NameCollisionOption.GenerateUniqueName);
    }

    /// <summary>
    /// 计算最终写入目标路径：
    /// - 不存在 → 直接返回；
    /// - 存在且内容相同(MD5) → 返回该路径（调用方按「未改动」处理）；
    /// - 存在且内容不同 → 按策略：Skip 返回 null（跳过），Overwrite 返回该路径，AutoRename 追加 _1/_2 直到唯一（同样以 MD5 判定是否重复内容）。
    /// </summary>
    private async Task<string?> ResolveTargetAsync(string output, string candidate, string md5, ConflictStrategy conflict, CancellationToken ct)
    {
        string target = Path.Combine(output, candidate);
        if (!File.Exists(target)) return target;

        string existing = await _hash.TryComputeMd5Async(target, ct).ConfigureAwait(false) ?? "";
        if (!string.IsNullOrEmpty(md5) && md5 == existing)
            return target; // 内容相同，无需动作

        return conflict switch
        {
            ConflictStrategy.Skip => null,
            ConflictStrategy.Overwrite => target,
            _ => await SuffixUntilFreeAsync(output, candidate, md5, ct).ConfigureAwait(false),
        };
    }

    private async Task<string> SuffixUntilFreeAsync(string output, string candidate, string md5, CancellationToken ct)
    {
        string name = Path.GetFileNameWithoutExtension(candidate);
        string ext = Path.GetExtension(candidate);
        string target = Path.Combine(output, candidate);
        int i = 1;
        while (File.Exists(target))
        {
            string em = await _hash.TryComputeMd5Async(target, ct).ConfigureAwait(false) ?? "";
            if (!string.IsNullOrEmpty(md5) && md5 == em)
                return target; // 该序号名下已是相同内容
            target = Path.Combine(output, $"{name}_{i}{ext}");
            if (++i > 9999) break;
        }

        return target;
    }

    private static string BuildName(PhotoFile f, string template, DateTime when, int index)
    {
        string San(string v) => Sanitize(v);
        string Ai(string v) => string.IsNullOrWhiteSpace(v) ? "unknown" : San(v);

        return (template ?? "")
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
        if (e.Status.Contains("去重") || e.Status.Contains("跳过") || e.Status.Contains("未改动"))
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
            return new CustomImageAnalysisService(req.CustomApiUrl, req.CustomApiModel, req.AiApiKey);
        }

        return req.AiProvider switch
        {
            AiProvider.Zhipu => new ZhipuImageAnalysisService(req.AiApiKey),
            AiProvider.Qwen => new QwenImageAnalysisService(req.AiApiKey),
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

    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            if (!_paused) return Task.CompletedTask;
            waiter = _waiter;
        }

        if (waiter == null) return Task.CompletedTask;
        if (ct.IsCancellationRequested)
        {
            waiter.TrySetResult(true);
            return Task.CompletedTask;
        }

        // 取消时放行，使循环顶部的 ThrowIfCancellationRequested 能抛出，避免暂停态下取消死锁。
        ct.Register(() => waiter.TrySetResult(true));
        return waiter.Task;
    }
}
