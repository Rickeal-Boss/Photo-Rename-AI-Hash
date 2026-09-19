using System;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// 永久性失败：重试无法恢复的错误。调用方（<c>OrganizeService.RunAsync</c>）见此类型即标记失败，
/// 不再把文件重排队重试——避免对同一文件刷 10 行「重试」并放大无效请求。
/// 与「瞬时故障」严格区分：网络抖动、429 限流（1302/1305）等仍走用户已裁定的重试设计
/// ——按供应商策略档退避（见 <see cref="AiRetryPolicy"/>，默认最多 8 次 / 总预算 180s）
/// + 单文件 10 次重排队。
/// </summary>
public class PermanentOperationException : Exception
{
    /// <summary>
    /// 是否为「环境级」错误（磁盘空间不足、权限未授予、备份路径不可访问等）：
    /// 这类错误对整批文件都成立，继续处理后续文件无意义，应中止整批并向上报告。
    /// 非环境级的永久错误（如某个 AI 账户欠费）仍先按单文件失败处理：不重排队重试该文件，
    /// 但若连续多个文件都命中且 <see cref="IsBatchLevel"/> 为 true，编排层会中止整批。
    /// </summary>
    public bool IsEnvironmentError { get; }

    /// <summary>
    /// 该错误是否对「整批」成立（而非仅当前这一个文件）。
    /// <c>true</c>（默认）：账户欠费 / 额度耗尽 / 模型不存在 / 参数非法 / 401/403/404 —— 对同批每个文件都成立。
    /// <c>false</c>：仅逐文件成立，例如某个文件本身损坏或缺少编解码器（HEIC）——
    /// 应当「该文件的失败不重试，但继续处理其它文件」，不得计入批次熔断。
    /// 注意：默认取 true 是为既有调用点（账户/模型配置类）保持行为不变；<b>新增</b>永久错误时
    /// 若其语义为「仅当前这一个文件」，必须显式传 false，否则会被静默纳入批次熔断。
    /// </summary>
    public bool IsBatchLevel { get; }

    public PermanentOperationException(string message, bool isEnvironmentError = false,
                                       bool isBatchLevel = true, Exception? inner = null)
        : base(message, inner)
    {
        IsEnvironmentError = isEnvironmentError;
        IsBatchLevel = isBatchLevel;
    }
}
