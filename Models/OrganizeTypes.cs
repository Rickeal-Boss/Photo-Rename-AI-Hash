using System.Collections.Generic;

namespace PhotoRenameAIHash.Models;

/// <summary>整理/归档的一次性请求参数。</summary>
public class OrganizeRequest
{
    public string SourceFolder { get; set; } = "";

    /// <summary>输出/归档目标文件夹；重命名模式下可留空（等同源文件夹）。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>重命名模式实际执行前的备份文件夹；为空表示未选择（将触发选择弹窗）。</summary>
    public string BackupFolder { get; set; } = "";

    public OperationMode Mode { get; set; } = OperationMode.Copy;

    public string NamingTemplate { get; set; } = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    public ConflictStrategy Conflict { get; set; } = ConflictStrategy.AutoRename;

    /// <summary>为 true 时只预测、不改写任何文件。</summary>
    public bool DryRun { get; set; }

    public AiProvider AiProvider { get; set; } = AiProvider.None;

    public string AiApiKey { get; set; } = "";

    /// <summary>自定义 API 端点（AiProvider.Custom 时使用）。</summary>
    public string CustomApiUrl { get; set; } = "";

    /// <summary>自定义模型名（AiProvider.Custom 时使用）。</summary>
    public string CustomApiModel { get; set; } = "";

    /// <summary>自定义引擎的每分钟请求上限（AiProvider.Custom 时使用）：0 = 不限。</summary>
    public int CustomApiRpmLimit { get; set; } = 0;

    public string Language { get; set; } = "zh-CN";

    public bool UseExifDate { get; set; } = true;

    /// <summary>
    /// 本次运行的「参数指纹」：由 <c>OrganizeService</c> 在批次开始时按「影响输出文件名 / 目标路径」
    /// 的参数算出（模式 / 冲突策略 / 命名模板 / 取 EXIF 日期 / 识别引擎 / 输出目录…），
    /// <b>调用方（ViewModel）无需也不应设置</b>，默认空串仅供构造期占位。
    /// 用途：写入 rename_log.csv，供下次续传时比对——指纹不同的历史记录不参与「已处理」跳过判定，
    /// 避免用户改模板 / 换模式 / 换冲突策略后重跑，整批被旧记录静默跳过。
    /// </summary>
    public string Fingerprint { get; set; } = "";
}

/// <summary>整理进度回调（在 UI 线程上派发）。</summary>
public class OrganizeProgress
{
    public int Percent { get; set; }

    public string Message { get; set; } = "";

    public string? LogLine { get; set; }

    public RenameLogEntry? Result { get; set; }

    /// <summary>
    /// AI 实际速率（张/分）：本批次「最近 60 秒窗口」内真正发出过 HTTP 的 AI 请求完成数。
    /// 与限流闸门同取 60 秒窗口，故可直接和 <see cref="AiGateRpm"/> 比较。
    /// null = 无有效样本（未启用 AI / 真实请求不足 2 次 / 全部命中结果缓存），UI 应隐藏速率行。
    /// </summary>
    public double? AiRpm { get; set; }

    /// <summary>
    /// 最近一次真实 AI 请求的端到端响应时长（毫秒）：含退避等待与重试，即「一张图从发请求到拿到结果」。
    /// null = 本批次尚无真实请求。
    /// </summary>
    public int? AiLatencyMs { get; set; }

    /// <summary>
    /// 当前引擎的限流闸门上限（张/分）；null = 该引擎不带闸门（不主动限速）。
    /// <b>它是「60 秒最多放行几次」的上限保护，不是速率目标</b>——与 <see cref="AiRpm"/> 并排显示
    /// 就是为了让这个区别可见（否则用户会把「闸门 30」读成「应该跑 30 张/分」）。
    /// </summary>
    public int? AiGateRpm { get; set; }
}

/// <summary>整理结果汇总。</summary>
public class OrganizeReport
{
    public int Total { get; set; }

    public int Processed { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public List<RenameLogEntry> Results { get; set; } = new();
}
