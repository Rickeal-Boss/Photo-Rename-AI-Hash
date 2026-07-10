namespace PhotoRenameAIHash.Models;

/// <summary>AI 视觉识别引擎。</summary>
public enum AiProvider
{
    None,
    Zhipu,
    Qwen,
    /// <summary>自定义 OpenAI 兼容端点（URL + 模型名 + API Key 均手动填写）。</summary>
    Custom
}
