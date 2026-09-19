namespace PhotoRenameAIHash.Models;

/// <summary>AI 视觉识别引擎。</summary>
public enum AiProvider
{
    None,
    Zhipu,
    Qwen,
    /// <summary>自定义 OpenAI 兼容端点（URL + 模型名 + API Key 均手动填写）。</summary>
    Custom,
    /// <summary>NVIDIA build.nvidia.com 托管 API（Nemotron Omni 视觉模型，内置 30 RPM 自动节流）。</summary>
    Nvidia
}
