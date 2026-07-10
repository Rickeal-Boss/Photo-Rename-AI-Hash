namespace PhotoRenameAIHash.Models;

/// <summary>AI 视觉识别返回的结构化结果，对应命名模板的六个 AI 占位符。</summary>
public class ImageAnalysisResult
{
    public string Category { get; set; } = "";
    public string Scene { get; set; } = "";
    public string People { get; set; } = "";
    public string Action { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Source { get; set; } = "";
}
