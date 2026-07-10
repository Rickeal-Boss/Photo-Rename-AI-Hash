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

    public QwenImageAnalysisService(string apiKey) => _apiKey = apiKey;

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
