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
        if (dataUrl == null) return null;

        var raw = await ImageAnalysisHelper.CallVisionApiAsync(Endpoint, Model, _apiKey,
            ImageAnalysisHelper.BuildPrompt(language), dataUrl, ct).ConfigureAwait(false);
        if (raw == null) return null;

        string? content = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        catch
        {
            return null;
        }

        return ImageAnalysisHelper.Parse(content);
    }
}
