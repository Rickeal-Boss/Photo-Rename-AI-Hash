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

    /// <summary>可选的「退避等待」替换钩子（语义见 <see cref="ImageAnalysisHelper.CallVisionApiAsync"/> 的 delayAsync）：
    /// null（默认）时行为与改造前完全一致（内部 Task.Delay）；非 null 时 AI 退避等待改走它，
    /// 使「暂停」能打断退避（组织层的暂停检查点夹在 AI 调用前后，退避期间点暂停原本要等约 4 分钟）。</summary>
    private readonly Func<TimeSpan, CancellationToken, Task>? _delayAsync;

    /// <summary>命名模板：用于裁剪提示词——只要求模型输出模板真正用到的字段。
    /// 为 null 时按全部字段兜底。</summary>
    private readonly string? _template;

    public ZhipuImageAnalysisService(string apiKey, string? template = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _apiKey = apiKey;
        _template = template;
        _delayAsync = delayAsync;
    }

    public async Task<ImageAnalysisResult?> AnalyzeAsync(string imagePath, string language, CancellationToken ct = default)
    {
        // 解码失败由 EncodeAsJpegDataUrlAsync 直接抛 AiPermanentException（isBatchLevel: false，
        // 逐文件不重试、不熔断整批），并携带脱敏后的真因与 inner——此处不再做 null 兜底（P1-6）。
        var dataUrl = await ImageAnalysisHelper.EncodeAsJpegDataUrlAsync(imagePath, 1024, ct).ConfigureAwait(false);

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
                        new { type = "text", text = ImageAnalysisHelper.BuildPrompt(language, _template) },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            },
            temperature = 0.3,
            max_tokens = MaxTokens,
            thinking = new { type = "disabled" },
        };

        // 走 Zhipu 策略档：智谱限流维度是「并发数」而非 RPM，本应用的 AI 调用严格串行
        //（同一时刻在途请求恒为 1），已天然规避 1302 并发超限，故该档不带 RPM 闸门（加了只会人为降速）。
        // 档位同时承载智谱的永久错误业务码表（欠费/额度/套餐类 429 立即失败，1302/1305 仍重试）。
        // CallVisionApiRawAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null。
        var raw = await ImageAnalysisHelper.CallVisionApiRawAsync(
            Endpoint, _apiKey, JsonSerializer.Serialize(body), AiProviderProfiles.For(AiProvider.Zhipu), ct, _delayAsync, imagePath).ConfigureAwait(false);

        // 传入文件名：让「空正文」这条逐文件级永久错误的文案自带文件名，
        // 避免同因计数把它误当成整批级根因熔断整批（P26 三层口径）。
        var content = ImageAnalysisHelper.ExtractContent(raw, System.IO.Path.GetFileName(imagePath));
        var result = ImageAnalysisHelper.Parse(content, _template);
        // 与 CustomImageAnalysisService 同口径：解析不出抛 AiResultInvalidException（重试有意义），
        // 不判永久（temperature=0.3 下输出非确定性；判永久会让连续 3 个文件熔断整批，见 P26）。
        // 截断类确定性失败已由 ImageAnalysisHelper.ExtractContent 精确短路，不走这里。
        return result ?? throw new AiResultInvalidException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}。" +
            "若同一批反复出现，请更换识别模型或检查模型名；偶发情况会自动重试该文件。");
    }
}
