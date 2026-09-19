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
    private readonly int _rpmLimit;

    /// <param name="endpoint">完整的 chat/completions 端点 URL</param>
    /// <param name="model">模型名，如 gpt-4o</param>
    /// <param name="apiKey">API Key</param>
    /// <param name="rpmLimit">每分钟请求上限（用户在「设置」填写）；0 = 不限（不替用户猜 RPM），
    /// &gt; 0 时覆盖端点嗅探得出的闸门。</param>
    public CustomImageAnalysisService(string endpoint, string model, string apiKey, int rpmLimit = 0)
    {
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _rpmLimit = rpmLimit;
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

        // 走端点 host 嗅探出的策略档（智谱/阿里/Anthropic/Gemini 各有档位，未命中走通用档）；
        // 用户在设置里填了「每分钟请求上限」时，在该档位基础上覆盖闸门。
        // CallVisionApiAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null
        var raw = await ImageAnalysisHelper.CallVisionApiAsync(_endpoint, _model, _apiKey,
            ImageAnalysisHelper.BuildPrompt(language), dataUrl,
            AiProviderProfiles.For(AiProvider.Custom, _endpoint, _rpmLimit), ct).ConfigureAwait(false);

        var content = ImageAnalysisHelper.ExtractContent(raw);
        var result = ImageAnalysisHelper.Parse(content);
        return result ?? throw new InvalidOperationException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}");
    }
}
