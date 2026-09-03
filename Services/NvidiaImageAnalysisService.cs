using System;
using System.Net.Http;
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
/// 官方免费档限流 ~40 RPM（按 nvapi key 账户级共享、跨模型共用同一额度）：
/// 本引擎独享一枚进程级滑动窗口闸门（60 秒最多 40 次），在每次真实 HTTP 尝试（含重试）
/// 前先取名额，主动把请求速率压在限流之下，而非被动吃 429 等重试；
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

    /// <summary>官方免费档 ~40 RPM（按 key 账户级共享）：滑动窗口 60 秒最多 40 次；
    /// 进程级共享，跨整理批次仍然生效（同一 key 的额度本就是连续计费的）。</summary>
    private static readonly RateGate RateLimit = new(40, TimeSpan.FromMinutes(1));

    private readonly string _apiKey;

    public NvidiaImageAnalysisService(string apiKey) => _apiKey = apiKey;

    public async Task<ImageAnalysisResult?> AnalyzeAsync(string imagePath, string language, CancellationToken ct = default)
    {
        var dataUrl = await ImageAnalysisHelper.EncodeAsJpegDataUrlAsync(imagePath, 1024, ct).ConfigureAwait(false);
        if (dataUrl == null)
            throw new InvalidOperationException($"无法解码图片（可能不是有效图像或已损坏）：{System.IO.Path.GetFileName(imagePath)}");

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

        // CallVisionApiRawAsync 在密钥/网络/HTTP 异常时抛异常，不会返回 null；
        // RateLimit 闸门在每次真实 HTTP 尝试（含重试）前生效。
        string raw;
        try
        {
            raw = await ImageAnalysisHelper.CallVisionApiRawAsync(
                Endpoint, _apiKey, JsonSerializer.Serialize(body), RateLimit, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("返回 403 "))
        {
            // NIM 已知机制：部分模型族需先在 build.nvidia.com 该模型页面点击一次「Try API」注册，
            // Key 才获得调用权限，否则一律 403。裸状态码对用户不可理解，此处给定向修复指引。
            throw new InvalidOperationException(
                "NVIDIA 接口返回 403：该模型族可能尚未在你的账号下注册（或 Key 无该模型权限）。" +
                "请先打开 build.nvidia.com 的 nemotron-3-nano-omni-30b-a3b-reasoning 模型页面点击一次「Try API」再重试。" +
                $"原始错误：{ex.Message}", ex);
        }

        var content = ImageAnalysisHelper.ExtractNemotronContent(raw);
        var result = ImageAnalysisHelper.Parse(content);
        return result ?? throw new InvalidOperationException(
            $"视觉识别返回内容无法解析为结构化结果（模型可能未按要求返回 JSON）：{System.IO.Path.GetFileName(imagePath)}");
    }
}
