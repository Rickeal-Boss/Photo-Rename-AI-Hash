using System;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// AI 视觉识别接口的「<b>瞬时故障</b>」：网络 / 连通性异常、服务端 60 秒未响应（超时）等
/// ——单次调用内的退避重试已耗尽（次数上限或 180s 预算），但换个时间点重试<b>有真实成功率</b>。
/// <para>调用方（<c>OrganizeService.RunAsync</c>）见此类型按用户已裁定的口径走「单文件 10 次重排队」，
/// 与「确定性失败」（解析不出结构化结果、接口 4xx、配置缺失、解码失败、MD5 读不出来）的 2 次严格区分：
/// 网络类宁可慢，也不产出 <c>unknown_</c> 垃圾名。</para>
/// <para>与 <see cref="AiPermanentException"/> 严格区分：后者重试无意义（账户欠费 / 额度耗尽 /
/// 模型不存在 / 参数非法 / 输出被 max_tokens 截断），不重排队。</para>
/// <para><b>为什么需要这个专用类型：</b>此前这两类故障抛的是裸 <see cref="InvalidOperationException"/>，
/// 与「配置缺失 / 端点为空 / 解析失败」同型，编排层只能靠「inner 链里有没有
/// <see cref="System.Net.Http.HttpRequestException"/> / <see cref="TimeoutException"/>」反推
/// ——判据脆弱（HttpClient 抛出的原始异常类型随 .NET 版本与底层 IO 实现变化，
/// 例如某些平台上直接是 <see cref="System.IO.IOException"/> 或 <c>SocketException</c>，
/// 那样就会被误判成确定性失败、只重排 2 次）。有了本类型，编排层可一行按类型精确分档。</para>
/// <para>原始异常<b>必须</b>通过 <c>inner</c> 保留：既便于排障，也让既有的 inner 链判据继续成立（双重保险）。</para>
/// </summary>
public sealed class AiTransientException : Exception
{
    /// <param name="message">面向用户的中文文案（须说明「已尝试几次 / 累计多少秒 / 为什么放弃」）。</param>
    /// <param name="inner">底层原始异常（如 <see cref="System.Net.Http.HttpRequestException"/>、
    /// 或 inner 为 <see cref="TimeoutException"/> 的 <see cref="TaskCanceledException"/>）。<b>不得丢弃</b>。</param>
    public AiTransientException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
