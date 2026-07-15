using System.Collections.Generic;

namespace PhotoRenameAIHash.Models;

public class DuplicateGroup
{
    public int Id { get; set; }

    public List<PhotoFile> Members { get; set; } = new();

    /// <summary>Index (into Members) of the file to keep when deleting duplicates.</summary>
    public int KeepIndex { get; set; } = 0;

    /// <summary>
    /// 精确哈希模式下各成员共同的 MD5（内容完全相同判定依据）。
    /// 感知哈希(相似)模式下为空字符串。
    /// </summary>
    public string Hash { get; set; } = "";
}
