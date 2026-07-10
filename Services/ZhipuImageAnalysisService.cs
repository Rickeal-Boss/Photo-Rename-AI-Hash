using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>智谱 GLM-4V 视觉识别（OpenAI 兼容接口）。</summary>
public sealed class ZhipuImageAnalysisService : IImageAnalysisService
{
    public AiProvider Provider => AiProvider.Zhipu;

    private const string Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
    private const string Model = "glm-4v-plus";

    private readonly string _apiKey;

    public ZhipuImageAnalysisService(string apiKey) => _apiKey = apiKey;

    public async Task<ImageAnalysisResult?> AnalyzeAsync(string imagePath, string language, CancellationToken ct = default)
    {
        var dataUrl = await ImageAnalysisHelper.EncodeAsJpegDataUrlAsync(imagePath, 1024, ct).ConfigureAwait(false);
        if (dataUrl == null)
            throw new InvalidOperationException($"无法解码图片（可能不是有效图像或已损坏）：{System.IO.Path.GetFileName(imagePath)}");

        // CallVisionApiAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null
        var raw = await ImageAnalysisHelper.CallVisionApiAsync(Endpoint, Model, _apiKey,
            ImageAnalysisHelper.BuildPrompt(language), dataUrl, ct).ConfigureAwait(false);

        var content = ImageAnalysisHelper.ExtractContent(raw);
        var result = ImageAnalysisHelper.Parse(content);
        return result ?? throw new InvalidOperationException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}");
    }
}
