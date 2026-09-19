using System;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// AI 视觉识别接口返回的「永久性错误」：账户欠费、额度耗尽、套餐到期、无模型权限、
/// Key 类型不匹配、模型名不存在等。此类错误重试无法恢复，调用方应直接标记失败，
/// 不要进入文件级重试队列（否则同一文件会在 UI 上刷 10 行「重试」，且概念上不该重试）。
/// 与「瞬时限流」严格区分：HTTP 429 + 业务码 1302（并发超限）/ 1305（平台过载）仍走
/// 15×15s 重试 + 单文件 10 次重排队，属用户已裁定的有意设计，不得改变。
/// 继承 <see cref="PermanentOperationException"/> 以复用「不重试」语义；
/// <c>IsEnvironmentError</c> 取 false——AI 账户问题虽对同批其它文件也成立，但按既定口径
/// 仍按单文件失败处理（不改变现行为）。
/// </summary>
public sealed class AiPermanentException : PermanentOperationException
{
    public AiPermanentException(string message) : base(message, isEnvironmentError: false)
    {
    }
}
