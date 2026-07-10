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

    /// <param name="endpoint">完整的 chat/completions 端点 URL</param>
    /// <param name="model">模型名，如 gpt-4o</param>
    /// <param name="apiKey">API Key</param>
    public CustomImageAnalysisService(string endpoint, string model, string apiKey)
    {
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
    }

    public async Task<ImageAnalysisResult?> AnalyzeAsync(string imagePath, string language, CancellationToken ct = default)
    {
        var dataUrl = await ImageAnalysisHelper.EncodeAsJpegDataUrlAsync(imagePath, 1024, ct).ConfigureAwait(false);
        if (dataUrl == null) return null;

        var raw = await ImageAnalysisHelper.CallVisionApiAsync(_endpoint, _model, _apiKey,
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
