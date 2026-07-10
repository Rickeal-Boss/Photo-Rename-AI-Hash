namespace PhotoRenameAIHash.Models;

/// <summary>目标位置已存在同名文件时的处理策略。</summary>
public enum ConflictStrategy
{
    /// <summary>自动添加序号后缀 _1、_2 … 以避免覆盖；若内容完全相同(MD5 一致)则视为已存在、跳过。</summary>
    AutoRename,

    /// <summary>跳过该文件，不覆盖也不重命名。</summary>
    Skip,

    /// <summary>直接覆盖已存在的文件。</summary>
    Overwrite
}
