using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Helpers;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

/// <summary>
/// NVIDIA build.nvidia.com（NIM 托管 API）视觉识别：
/// <c>nvidia/nemotron-3-nano-omni-30b-a3b-reasoning</c>，端点
/// <c>https://integrate.api.nvidia.com/v1/chat/completions</c>（OpenAI 兼容）。
/// 官方免费档限流按 nvapi key 账户级共享（跨模型共用同一额度）：
/// 本引擎使用进程级滑动窗口闸门（60 秒最多 30 次，见 <see cref="AiProviderProfiles.Nvidia"/>），
/// 在每次真实 HTTP 尝试（含重试）前先取名额，主动把请求速率压在限流之下，而非被动吃 429 等重试；
/// 40 RPM 仅有论坛佐证、官方文档无速率限制章节，而串行调用实际只有 6~10 RPM，
/// 故取 30（降速成本≈0，白拿 25% 余量保险）。
/// 闸门只作用于本引擎，智谱/通义/自定义引擎的调用策略完全不受影响。
/// reasoning 模型按官方文档默认开启 thinking：本引擎显式关闭（照片命名无需思维链，
/// 且官方警告 max_tokens 偏低时推理阶段会耗尽预算导致 content 为空）；
/// 解析层另保留 reasoning 字段回退与 think 段剥离兜底，确保即使 thinking 未被完全
/// 关闭也能从中提取最终 JSON。
/// </summary>
public sealed class NvidiaImageAnalysisService : IImageAnalysisService
{
    public AiProvider Provider => AiProvider.Nvidia;

    private const string Endpoint = "https://integrate.api.nvidia.com/v1/chat/completions";
    private const string Model = "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning";

    /// <summary>闸门已迁入 <see cref="AiProviderProfiles.Nvidia"/>：滑动窗口 60 秒最多 30 次，
    /// 进程级共享，跨整理批次仍然生效（同一 key 的额度本就是连续计费的）。</summary>
    private readonly string _apiKey;

    /// <summary>可选的「退避等待」替换钩子（语义见 <see cref="ImageAnalysisHelper.CallVisionApiAsync"/> 的 delayAsync）：
    /// null（默认）时行为与改造前完全一致（内部 Task.Delay）；非 null 时 AI 退避等待改走它，
    /// 使「暂停」能打断退避（组织层的暂停检查点夹在 AI 调用前后，退避期间点暂停原本要等约 4 分钟）。</summary>
    private readonly Func<TimeSpan, CancellationToken, Task>? _delayAsync;

    public NvidiaImageAnalysisService(string apiKey, Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
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

        // 自定义请求体：reasoning 模型官方约束——显式关闭 thinking；max_tokens 给足余量防 JSON 截断。
        // image_url 用 base64 data URL（官方文档明确支持），与其它引擎共用同一图片压缩管线。
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
            max_tokens = 1024,
            chat_template_kwargs = new { enable_thinking = false },
        };

        // 走 Nvidia 策略档（本仓唯一默认带 RPM 闸门的档位：60 秒 30 次），
        // 闸门在每次真实 HTTP 尝试（含重试）前生效。
        // CallVisionApiRawAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null；
        // 4xx（含 403）由 CallVisionApiRawAsync 直接抛 AiPermanentException（并附 403 定向引导），
        // 故本方法不再按状态码做任何二次包装——这里也确实没有任何 catch / 异常过滤器。
        // （旧注释曾写「HttpRequestException 过滤器已永不命中（死代码）」，但那段过滤器早随
        //   状态码判定上移而删除，注释指向不存在的代码，会误导维护者，故改为上述准确描述。）
        var raw = await ImageAnalysisHelper.CallVisionApiRawAsync(
            Endpoint, _apiKey, JsonSerializer.Serialize(body),
            AiProviderProfiles.For(AiProvider.Nvidia), ct, _delayAsync).ConfigureAwait(false);

        var content = ImageAnalysisHelper.ExtractNemotronContent(raw);
        var result = ImageAnalysisHelper.Parse(content);
        // 与 CustomImageAnalysisService 同口径：解析不出保持「可重试」，不判永久
        // （temperature=0.3 下输出非确定性；判永久会让连续 3 个文件熔断整批，见 P26）。
        // 截断类确定性失败已由 ImageAnalysisHelper.ExtractContent 精确短路，不走这里。
        return result ?? throw new InvalidOperationException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}。" +
            "若同一批反复出现，请更换识别模型或检查模型名；偶发情况会自动重试该文件。");
    }
}
