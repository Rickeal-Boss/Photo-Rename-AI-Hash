using System;
using Microsoft.UI.Xaml;

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

    // ── 以下为界面展示用只读计算属性（P1-4 结果列表图标化三态互斥）──

    /// <summary>成功态：已复制 / 已移动 / 已重命名（兼容「成功」字样）。</summary>
    public bool IsSuccess =>
        Status.Contains("成功") || Status.Contains("已复制") ||
        Status.Contains("已移动") || Status.Contains("已重命名");

    /// <summary>失败态：错误 / 失败。</summary>
    public bool IsError => Status.Contains("错误") || Status.Contains("失败");

    /// <summary>中性态：跳过 / 未改动 / 模拟等，非成功也非失败。</summary>
    public bool IsNeutral => !IsSuccess && !IsError;

    // Visibility 化计算属性：供 DataTemplate 中 FontIcon 的 Visibility 直接 x:Bind
    // （x:Bind 无法把 bool 隐式转换为 Visibility，且省去新增转换器资源）

    /// <summary>成功图标可见性。</summary>
    public Visibility IsSuccessVisibility => IsSuccess ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>失败图标可见性。</summary>
    public Visibility IsErrorVisibility => IsError ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>中性图标可见性。</summary>
    public Visibility IsNeutralVisibility => IsNeutral ? Visibility.Visible : Visibility.Collapsed;
}
