namespace PhotoRenameAIHash.Models;

public class PhotoFile
{
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    public long SizeBytes { get; set; }

    public System.DateTime LastModified { get; set; }

    // ---- 扩展字段：供整理流程使用 ----

    /// <summary>源文件 MD5，用于冲突检测（内容相同则视为同一文件，跳过或加序号）。</summary>
    public string Md5 { get; set; } = "";

    // AI 识别结果（对应命名模板的占位符）
    public string Category { get; set; } = "";
    public string Scene { get; set; } = "";
    public string People { get; set; } = "";
    public string Action { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string SourceTag { get; set; } = "";
}
