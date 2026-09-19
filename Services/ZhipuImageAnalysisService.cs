using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>智谱 GLM-4.6V-Flash 视觉识别（OpenAI 兼容接口，默认免费模型）。
/// 产品展示名 "GLM-4.6V-Flash" 仅为人类可读标签，实际 API 调用必须使用官方小写编码 <c>glm-4.6v-flash</c>。</summary>
public sealed class ZhipuImageAnalysisService : IImageAnalysisService
{
    public AiProvider Provider => AiProvider.Zhipu;

    private const string Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";

    /// <summary>官方模型编码为全小写（产品展示名 "GLM-4.6V-Flash" 不可用于 API 调用）。
    /// 大小写敏感时错误码 1211「模型不存在」返回 HTTP 400，且 4xx 不重试 → 整批立即失败，
    /// 故此处严格使用官方编码。</summary>
    private const string Model = "glm-4.6v-flash";

    /// <summary>官方默认 16384、建议 ≥1024。原 400 在思考模式下会被思维链耗尽导致正文为空。</summary>
    private const int MaxTokens = 1024;

    private readonly string _apiKey;

    public ZhipuImageAnalysisService(string apiKey) => _apiKey = apiKey;

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

        // 自定义请求体：照片命名无需思维链，显式关闭思考模式（智谱参数为顶层 thinking.type，
        // 取值 enabled/disabled —— 注意不是 NVIDIA NIM 的 chat_template_kwargs.enable_thinking），
        // 并给足输出预算，避免 JSON 被截断。
        var body = new
        {
            model = Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = ImageAnalysisHelper.BuildPrompt(language) },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            },
            temperature = 0.3,
            max_tokens = MaxTokens,
            thinking = new { type = "disabled" },
        };

        // gate 传 null：智谱限流以「并发数」为维度，本应用的 AI 调用严格串行（同一时刻在途请求恒为 1），
        // 已天然规避 1302 并发超限；额外加闸门只会人为降速，无收益。
        // CallVisionApiRawAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null。
        var raw = await ImageAnalysisHelper.CallVisionApiRawAsync(
            Endpoint, _apiKey, JsonSerializer.Serialize(body), gate: null, ct).ConfigureAwait(false);

        var content = ImageAnalysisHelper.ExtractContent(raw);
        var result = ImageAnalysisHelper.Parse(content);
        return result ?? throw new InvalidOperationException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}");
    }
}
