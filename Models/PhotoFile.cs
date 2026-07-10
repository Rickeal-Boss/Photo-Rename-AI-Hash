namespace PhotoRenameAIHash.Models;

public class PhotoFile
{
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    public long SizeBytes { get; set; }

    public System.DateTime LastModified { get; set; }

    public string? NewName { get; set; }

    public string? Status { get; set; }

    // ---- 扩展字段：供整理流程使用 ----

    /// <summary>EXIF 拍摄时间（若可读取），否则为 null。</summary>
    public System.DateTime? DateTaken { get; set; }

    /// <summary>源文件 MD5，用于冲突检测与去重判定。</summary>
    public string Md5 { get; set; } = "";

    public ulong AHash { get; set; }

    public ulong DHash { get; set; }

    // AI 识别结果（对应命名模板的占位符）
    public string Category { get; set; } = "";
    public string Scene { get; set; } = "";
    public string People { get; set; } = "";
    public string Action { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string SourceTag { get; set; } = "";
}
