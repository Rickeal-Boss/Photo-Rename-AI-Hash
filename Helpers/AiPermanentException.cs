using System;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// AI 视觉识别接口返回的「永久性错误」：账户欠费、额度耗尽、套餐到期、无模型权限、
/// Key 类型不匹配、模型名不存在等。此类错误重试无法恢复，调用方应直接标记失败，
/// 不要进入文件级重试队列（否则同一文件会在 UI 上刷 10 行「重试」，且概念上不该重试）。
/// 与「瞬时限流」严格区分：HTTP 429 + 业务码 1302（并发超限）/ 1305（平台过载）仍走
/// 「按供应商策略档退避重试（见 <see cref="AiRetryPolicy"/>，默认最多 8 次 / 总预算 180s）
/// + 单文件 10 次重排队」，属用户已裁定的有意设计，不得改变。
/// 继承 <see cref="PermanentOperationException"/> 以复用「不重试」语义；
/// <c>IsEnvironmentError</c> 取 false——AI 账户问题虽对同批其它文件也成立，但仍先按单文件
/// 失败处理：不重排队重试该文件；连续 3 个文件命中且 <c>IsBatchLevel</c> 为 true 时才中止整批
/// （单次调用内的退避重试与单文件 10 次重排队的设计意图均未改变）。
/// </summary>
public sealed class AiPermanentException : PermanentOperationException
{
    /// <param name="isBatchLevel">默认 true（账户/模型配置类，对整批成立）；
    /// 传入 false 表示仅逐文件成立（如图片本身无法解码），不参与批次熔断。</param>
    /// <param name="inner">底层原始异常（如解码失败的真实原因：<c>FileNotFoundException</c> /
    /// 缺编解码器的 <c>NotSupportedException</c> / 尺寸超限的 <c>InvalidOperationException</c>）。
    /// <b>不得丢弃</b>：归因靠它——「图片已损坏」与实际是「被云盘同步锁住 / HEIC 缺解码器」完全不同（P1-6 / P23），
    /// 保留 inner 才能让 <c>crash.log</c> 定位真因。默认 null 以保持既有调用点行为不变。</param>
    public AiPermanentException(string message, bool isBatchLevel = true, Exception? inner = null)
        : base(message, isEnvironmentError: false, isBatchLevel: isBatchLevel, inner: inner)
    {
    }
}
