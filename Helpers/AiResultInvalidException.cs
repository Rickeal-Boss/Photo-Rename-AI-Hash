using System;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// AI「<b>返回了内容，但内容不是我们能用的结果</b>」：
/// ① 响应体不是合法的 chat/completions JSON（网关/代理偶发返回 HTML 或纯文本错误页）；
/// ② 模型输出解析不出结构化结果（<see cref="ImageAnalysisHelper.Parse"/> 返回 null）。
/// <para>与两类既有异常严格区分：
/// <list type="bullet">
/// <item><description><see cref="AiPermanentException"/>：重试<b>无意义</b>（账户 / 额度 / 模型配置类，
/// 或 <c>finish_reason=length</c> 的 max_tokens 截断）。</description></item>
/// <item><description>网络 / 超时类：<see cref="ImageAnalysisHelper"/> 抛的是 <see cref="AiTransientException"/>
/// （其 inner 链里仍带 <see cref="System.Net.Http.HttpRequestException"/> 或 <see cref="TimeoutException"/>）。</description></item>
/// </list>
/// </para>
/// <para><b>本类型表示「重试是有意义的」</b>：temperature=0.3 下模型输出并非确定性（换个采样可能就合规），
/// 网关返回非 JSON 错误页也往往是瞬时的。此前这两类与「配置缺失 / 端点错误」共用裸
/// <see cref="InvalidOperationException"/>，编排层只能靠「inner 链里没有 HttpRequestException /
/// TimeoutException」这一粗糙判据反推，容易把「该多试几次」的样本一起压成 2 次。
/// 有了本类型，编排层可一行改为按类型精确分档。</para>
/// 故意继承 <see cref="InvalidOperationException"/>：既有 <c>catch (InvalidOperationException)</c>
/// 行为<b>完全不变</b>，本类型是纯增量，编排层不升级也能正常工作。
/// </summary>
public sealed class AiResultInvalidException : InvalidOperationException
{
    public AiResultInvalidException(string message) : base(message)
    {
    }

    /// <param name="inner">底层解析异常（如 <see cref="System.Text.Json.JsonException"/>）。
    /// <b>必须保留</b>：编排层靠 inner 链判断「是不是网络 / 超时」，丢 inner 会让分档失真。</param>
    public AiResultInvalidException(string message, Exception inner) : base(message, inner)
    {
    }
}
