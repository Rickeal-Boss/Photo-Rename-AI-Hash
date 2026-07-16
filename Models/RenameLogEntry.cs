using System;

namespace PhotoRenameAIHash.Models;

/// <summary>
/// 单条重命名/归档记录，既用于界面「处理结果」列表展示，也用于持久化重命名日志
/// （写入输出文件夹下的 rename_log.csv），保证批量操作的可追溯性。
/// </summary>
public class RenameLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string OriginalPath { get; set; } = "";

    public string OriginalName { get; set; } = "";

    public string NewPath { get; set; } = "";

    public string NewName { get; set; } = "";

    /// <summary>源文件 MD5，用于冲突检测（内容相同则跳过或加序号）。</summary>
    public string Md5 { get; set; } = "";

    /// <summary>操作类型：复制 / 移动 / 重命名 / 归档。</summary>
    public string Operation { get; set; } = "";

    /// <summary>处理结果状态，例如：已重命名 / 已复制 / 已移动 / 跳过(已存在) / 未改动(内容相同) / 错误。</summary>
    public string Status { get; set; } = "";

    public string Message { get; set; } = "";
}
