namespace PhotoRenameAIHash.Models;

public enum AppTheme
{
    Light,
    Dark,
    System
}

public class AppSettings
{
    public const string AppVersion = "1.0.0";

    public AppTheme Theme { get; set; } = AppTheme.System;

    public string DefaultFolder { get; set; } = "";

    /// <summary>Hamming-distance threshold for aHash grouping (0 = exact match).</summary>
    public int AHashThreshold { get; set; } = 8;

    /// <summary>Hamming-distance threshold for dHash grouping (0 = exact match).</summary>
    public int DHashThreshold { get; set; } = 10;

    public string Language { get; set; } = "zh-CN";

    // ---- 整理功能配置（持久化以便下次启动沿用） ----

    public OperationMode OperationMode { get; set; } = OperationMode.Copy;

    public string NamingTemplate { get; set; } = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    public ConflictStrategy ConflictStrategy { get; set; } = ConflictStrategy.AutoRename;

    public bool EnableDedup { get; set; }

    public string OutputFolder { get; set; } = "";

    /// <summary>为 true 时只模拟、不改写文件。</summary>
    public bool DryRun { get; set; }

    public bool UseExifDate { get; set; } = true;

    // ---- AI 视觉识别配置 ----

    public AiProvider AiProvider { get; set; } = AiProvider.None;

    public string ZhipuApiKey { get; set; } = "";

    public string QwenApiKey { get; set; } = "";
}
