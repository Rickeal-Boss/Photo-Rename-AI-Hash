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

    public string Language { get; set; } = "zh-CN";

    // ---- 整理功能配置（持久化以便下次启动沿用） ----

    public OperationMode OperationMode { get; set; } = OperationMode.Copy;

    public string NamingTemplate { get; set; } = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    public ConflictStrategy ConflictStrategy { get; set; } = ConflictStrategy.AutoRename;

    public string OutputFolder { get; set; } = "";

    /// <summary>为 true 时只模拟、不改写文件。</summary>
    public bool DryRun { get; set; }

    public bool UseExifDate { get; set; } = true;

    // ---- AI 视觉识别配置 ----

    public AiProvider AiProvider { get; set; } = AiProvider.None;

    public string ZhipuApiKey { get; set; } = "";

    public string QwenApiKey { get; set; } = "";

    /// <summary>NVIDIA build.nvidia.com API Key（形如 nvapi-…，官方免费档 ~40 RPM 限速已内置适配）。</summary>
    public string NvidiaApiKey { get; set; } = "";

    /// <summary>自定义 OpenAI 兼容 API 地址（如 https://api.openai.com/v1/chat/completions）。</summary>
    public string CustomApiUrl { get; set; } = "";

    /// <summary>自定义模型名（如 gpt-4o）。</summary>
    public string CustomApiModel { get; set; } = "";

    /// <summary>自定义 API Key。</summary>
    public string CustomApiKey { get; set; } = "";
}
