using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>通义千问 Qwen-VL 视觉识别（DashScope 兼容模式 / OpenAI 兼容接口）。</summary>
public sealed class QwenImageAnalysisService : IImageAnalysisService
{
    public AiProvider Provider => AiProvider.Qwen;

    private const string Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
    private const string Model = "qwen-vl-plus";

    private readonly string _apiKey;

    /// <summary>可选的「退避等待」替换钩子（语义见 <see cref="ImageAnalysisHelper.CallVisionApiAsync"/> 的 delayAsync）：
    /// null（默认）时行为与改造前完全一致（内部 Task.Delay）；非 null 时 AI 退避等待改走它，
    /// 使「暂停」能打断退避（组织层的暂停检查点夹在 AI 调用前后，退避期间点暂停原本要等约 4 分钟）。</summary>
    private readonly Func<TimeSpan, CancellationToken, Task>? _delayAsync;

    public QwenImageAnalysisService(string apiKey, Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _apiKey = apiKey;
        _delayAsync = delayAsync;
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

        // 走 Qwen 策略档：官方 1200 RPM/1M TPM 是 qwen-vl-max 的额度，而本应用串行调用实际只有
        // 3~20 RPM（差两个数量级）→ 该档不带闸门（闸门永不触发，加了只会人为降速）。
        // CallVisionApiAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null
        var raw = await ImageAnalysisHelper.CallVisionApiAsync(Endpoint, Model, _apiKey,
            ImageAnalysisHelper.BuildPrompt(language), dataUrl,
            AiProviderProfiles.For(AiProvider.Qwen), ct, _delayAsync).ConfigureAwait(false);

        var content = ImageAnalysisHelper.ExtractContent(raw);
        var result = ImageAnalysisHelper.Parse(content);
        // 与 CustomImageAnalysisService 同口径：解析不出保持「可重试」，不判永久
        // （temperature=0.3 下输出非确定性；判永久会让连续 3 个文件熔断整批，见 P26）。
        // 截断类确定性失败已由 ImageAnalysisHelper.ExtractContent 精确短路，不走这里。
        return result ?? throw new InvalidOperationException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}。" +
            "若同一批反复出现，请更换识别模型或检查模型名；偶发情况会自动重试该文件。");
    }
}
