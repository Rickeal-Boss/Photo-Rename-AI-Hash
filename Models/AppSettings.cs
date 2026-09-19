namespace PhotoRenameAIHash.Models;

public enum AppTheme
{
    Light,
    Dark,
    System
}

public class AppSettings
{
    /// <summary>应用版本号。与 <c>Package.appxmanifest</c> 的 Identity Version 前三段必须一致
    /// （CI 有阻断式断言校验）。发版需递增：MSIX 版本号不变会让「覆盖升级」语义失效。</summary>
    public const string AppVersion = "3.1.2";

    public AppTheme Theme { get; set; } = AppTheme.System;

    public string DefaultFolder { get; set; } = "";

    public string Language { get; set; } = "zh-CN";

    // ---- 整理功能配置（持久化以便下次启动沿用） ----

    public OperationMode OperationMode { get; set; } = OperationMode.Copy;

    public string NamingTemplate { get; set; } = "{category}_{scene}_{people}_{action}_{subtitle}_{source}";

    public ConflictStrategy ConflictStrategy { get; set; } = ConflictStrategy.AutoRename;

    public string OutputFolder { get; set; } = "";

    /// <summary>为 true 时只模拟、不改写文件。P0-2 修复：默认 true（安全默认），
    /// 新装用户首次使用先看到模拟预览，避免在「模拟运行」字样下真实改动文件。</summary>
    public bool DryRun { get; set; } = true;

    public bool UseExifDate { get; set; } = true;

    // ---- AI 视觉识别配置 ----

    public AiProvider AiProvider { get; set; } = AiProvider.None;

    public string ZhipuApiKey { get; set; } = "";

    public string QwenApiKey { get; set; } = "";

    /// <summary>NVIDIA build.nvidia.com API Key（形如 nvapi-…，本引擎内置 30 RPM 自动节流）。</summary>
    public string NvidiaApiKey { get; set; } = "";

    /// <summary>自定义 OpenAI 兼容 API 地址（如 https://api.openai.com/v1/chat/completions）。</summary>
    public string CustomApiUrl { get; set; } = "";

    /// <summary>自定义模型名（如 gpt-4o）。</summary>
    public string CustomApiModel { get; set; } = "";

    /// <summary>自定义 API Key。</summary>
    public string CustomApiKey { get; set; } = "";

    /// <summary>自定义引擎的「每分钟请求上限」（RPM 闸门）。0 = 不限（默认，行为与改动前一致）。
    /// 仅在端点有明确 RPM 限制时填写：到达上限会自动排队等待，不会报错也不会跳过文件。
    /// 非密钥的普通整数，<b>不进 DPAPI 加解密清单</b>。</summary>
    public int CustomApiRpmLimit { get; set; } = 0;
}
