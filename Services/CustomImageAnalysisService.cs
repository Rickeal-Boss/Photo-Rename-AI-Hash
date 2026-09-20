using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>自定义 OpenAI 兼容视觉识别（端点和模型名可由用户在设置中填写）。</summary>
public sealed class CustomImageAnalysisService : IImageAnalysisService
{
    public AiProvider Provider => AiProvider.Custom;

    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    /// <summary>本实例的策略档：<b>必须在构造函数里建一次</b>，不能在 <see cref="AnalyzeAsync"/> 里
    /// 逐图调用 <see cref="AiProviderProfiles.ForCustom"/>——闸门是进程级缓存的（见
    /// <c>AiProviderProfiles.CustomGates</c>），但档位对象每次都会重建；逐图重建虽不至于让限速失效，
    /// 却会重复做 host 嗅探与字典查找，且让「闸门是跨调用存活的共享实例」这一事实在调用点不可见。</summary>
    private readonly AiProviderProfile _profile;

    /// <summary>可选的「退避等待」替换钩子（语义见 <see cref="ImageAnalysisHelper.CallVisionApiAsync"/> 的 delayAsync）：
    /// null（默认）时行为与改造前完全一致（内部 Task.Delay）；非 null 时 AI 退避等待改走它，
    /// 使「暂停」能打断退避（组织层的暂停检查点夹在 AI 调用前后，退避期间点暂停原本要等约 4 分钟）。</summary>
    private readonly Func<TimeSpan, CancellationToken, Task>? _delayAsync;

    /// <param name="endpoint">完整的 chat/completions 端点 URL</param>
    /// <param name="model">模型名，如 gpt-4o</param>
    /// <param name="apiKey">API Key</param>
    /// <param name="rpmLimit">每分钟请求上限（用户在「设置」填写）；0 = 不限（不替用户猜 RPM），
    /// &gt; 0 时覆盖端点嗅探得出的闸门。</param>
    /// <param name="delayAsync">可选的退避等待替换钩子（见 <see cref="_delayAsync"/>）。</param>
    public CustomImageAnalysisService(string endpoint, string model, string apiKey, int rpmLimit = 0,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _delayAsync = delayAsync;
        // 端点 host 嗅探（智谱/阿里/Anthropic/Gemini 各有档位，未命中走通用档）；
        // 填了「每分钟请求上限」时在档位基础上挂闸门（闸门实例进程级共享，跨图片、跨批次生效）。
        _profile = AiProviderProfiles.For(AiProvider.Custom, endpoint, rpmLimit);
    }

    public async Task<ImageAnalysisResult?> AnalyzeAsync(string imagePath, string language, CancellationToken ct = default)
    {
        var dataUrl = await ImageAnalysisHelper.EncodeAsJpegDataUrlAsync(imagePath, 1024, ct).ConfigureAwait(false);
        if (dataUrl == null)
            // 损坏文件或缺少编解码器（如 HEIC）是逐文件的确定性失败：
            // 既定口径要求「逐文件报错、不影响其它文件」，但不需要对同一坏文件重试 10 次。
            // 故 isBatchLevel: false：不重试，但也不参与批次熔断。
            throw new AiPermanentException(
                $"无法解码图片（可能不是有效图像或已损坏）：{System.IO.Path.GetFileName(imagePath)}",
                isBatchLevel: false);

        // 用构造函数里建好的策略档（闸门实例进程级共享，跨图片/跨批次累计计数，限速才真的生效）。
        // CallVisionApiAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null
        var raw = await ImageAnalysisHelper.CallVisionApiAsync(_endpoint, _model, _apiKey,
            ImageAnalysisHelper.BuildPrompt(language), dataUrl, _profile, ct, _delayAsync).ConfigureAwait(false);

        var content = ImageAnalysisHelper.ExtractContent(raw);
        var result = ImageAnalysisHelper.Parse(content);
        // 解析不出结构化结果：抛专用类型 AiResultInvalidException 而非裸 InvalidOperationException——
        // 语义是「模型给了内容但内容不可用，重试有意义」（temperature=0.3 下输出并非确定性），
        // 让编排层能与「配置缺失 / 端点错误」分档计重排队次数。
        // 不改成 AiPermanentException：判永久会让「连续 3 个文件命中」直接熔断整批（P26 红线），代价大于收益。
        // 真正确定性的那一种（finish_reason=length 截断）已在 ImageAnalysisHelper.ExtractContent
        // 用「截断 + 解析不出」这一精确判据短路为整批级永久错误，不会走到这里。
        return result ?? throw new AiResultInvalidException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}。" +
            "若同一批反复出现，请更换识别模型或检查模型名；偶发情况会自动重试该文件。");
    }
}
