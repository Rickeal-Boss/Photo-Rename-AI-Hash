using System;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// 永久性失败：重试无法恢复的错误。调用方（<c>OrganizeService.RunAsync</c>）见此类型即标记失败，
/// 不再把文件重排队重试——避免对同一文件刷 10 行「重试」并放大无效请求。
/// 与「瞬时故障」严格区分：网络抖动、429 限流（1302/1305）等仍走用户已裁定的
/// 15×15s + 单文件 10 次重排队设计。
/// </summary>
public class PermanentOperationException : Exception
{
    /// <summary>
    /// 是否为「环境级」错误（磁盘空间不足、权限未授予、备份路径不可访问等）：
    /// 这类错误对整批文件都成立，继续处理后续文件无意义，应中止整批并向上报告。
    /// 非环境级的永久错误（如某个 AI 账户欠费）仍按单文件失败处理。
    /// </summary>
    public bool IsEnvironmentError { get; }

    public PermanentOperationException(string message, bool isEnvironmentError = false, Exception? inner = null)
        : base(message, inner)
    {
        IsEnvironmentError = isEnvironmentError;
    }
}
