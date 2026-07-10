using System.Collections.Generic;

namespace PhotoRenameAIHash.Models;

/// <summary>整理/归档的一次性请求参数。</summary>
public class OrganizeRequest
{
    public string SourceFolder { get; set; } = "";

    /// <summary>输出/归档目标文件夹；重命名模式下可留空（等同源文件夹）。</summary>
    public string OutputFolder { get; set; } = "";

    public OperationMode Mode { get; set; } = OperationMode.Copy;

    public string NamingTemplate { get; set; } = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    public ConflictStrategy Conflict { get; set; } = ConflictStrategy.AutoRename;

    public bool EnableDedup { get; set; }

    public int AHashThreshold { get; set; } = 8;

    public int DHashThreshold { get; set; } = 10;

    /// <summary>为 true 时只预测、不改写任何文件。</summary>
    public bool DryRun { get; set; }

    public AiProvider AiProvider { get; set; } = AiProvider.None;

    public string AiApiKey { get; set; } = "";

    public string Language { get; set; } = "zh-CN";

    public bool UseExifDate { get; set; } = true;
}

/// <summary>整理进度回调（在 UI 线程上派发）。</summary>
public class OrganizeProgress
{
    public int Percent { get; set; }

    public string Message { get; set; } = "";

    public string? LogLine { get; set; }

    public RenameLogEntry? Result { get; set; }
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
