using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// 视觉识别辅助：把本地图片压成 JPEG data-URL、构造提示词、调用 OpenAI 兼容的
/// 多模态接口、并把返回内容解析为 <see cref="ImageAnalysisResult"/>。
/// 具体端点/模型由 Zhipu / Qwen 实现各自提供。
/// </summary>
public static class ImageAnalysisHelper
{
    private static readonly HttpClient Http = new();

    public static async Task<string?> EncodeAsJpegDataUrlAsync(string path, int maxDim, CancellationToken ct)
    {
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            var b64 = await ImageDecoder.EncodeResizedJpegAsync(fs, maxDim, ct).ConfigureAwait(false);
            return "data:image/jpeg;base64," + b64;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildPrompt(string language)
    {
        var lang = (language ?? "").StartsWith("zh") ? "中文" : "English";
        return "Analyze this image. Output ONLY a JSON object (no markdown fences) with exactly these keys: " +
               "category, scene, people, action, subtitle, source. Values in " + lang + ". " +
               "category=one short word (e.g. food/pet/people/landscape/document/screenshot). " +
               "scene=place where the photo was taken. people=person names or 'none'. " +
               "action=short verb phrase describing what is happening. " +
               "subtitle=any visible on-screen text or 'none'. source=origin like phone/screenshot/camera. " +
               "Example: {\"category\":\"pet\",\"scene\":\"living room\",\"people\":\"none\",\"action\":\"sleeping\",\"subtitle\":\"none\",\"source\":\"phone\"}";
    }

    /// <summary>
    /// 调用视觉识别接口。失败时抛异常（而非静默返回 null），调用方据此提示具体原因。
    /// 遇 HTTP 429（限流）或 5xx（服务端错误）时按 <see cref="BackoffAsync"/> 自动等待重试；
    /// 重试期间不返回任何结果，因此调用方不会据此产出 <c>unknown_</c> 重命名；限流解除后继续。
    /// 4xx（非 429）为客户端永久错误，不重试、直接抛异常。
    /// </summary>
    /// <exception cref="InvalidOperationException">端点/模型/密钥为空，或网络/连通性异常（重试耗尽）。</exception>
    /// <exception cref="HttpRequestException">接口返回非成功状态码（携带状态码与响应体片段，重试耗尽）。</exception>
    public static async Task<string> CallVisionApiAsync(
        string endpoint, string model, string apiKey, string prompt, string dataUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("视觉识别端点 URL 未配置（自定义引擎请在「设置」中填写端点）。");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("视觉识别模型名未配置（自定义引擎请在「设置」中填写模型名）。");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API Key 未配置：请在「设置」中填写所选识别引擎的 Key 后再开始整理。");

        var body = new
        {
            model = model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            },
            temperature = 0.3,
            max_tokens = 400,
        };

        const int maxRetries = 5;
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // HttpRequestMessage 单次使用，每次重试都必须重新构造
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 网络/连通性异常：视为瞬时抖动可重试，耗尽后抛明确异常
                if (attempt < maxRetries)
                {
                    attempt++;
                    await BackoffAsync(attempt, null, ct).ConfigureAwait(false);
                    continue;
                }
                throw new InvalidOperationException(
                    $"调用视觉识别接口失败（网络/连通性，重试 {maxRetries} 次后仍失败）：{ex.Message}。请检查网络与端点 URL 是否正确。", ex);
            }

            using (resp)
            {
                var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                int code = (int)resp.StatusCode;

                // 429 限流 / 5xx 服务端错误：等待后重试，期间不做任何重命名
                if (code == 429 || code >= 500)
                {
                    if (attempt < maxRetries)
                    {
                        attempt++;
                        await BackoffAsync(attempt, resp, ct).ConfigureAwait(false);
                        continue;
                    }
                    var snippet = respText.Length > 500 ? respText.Substring(0, 500) : respText;
                    throw new HttpRequestException(
                        $"视觉识别接口限流/错误（{code} {resp.StatusCode}），重试 {maxRetries} 次后仍失败：{snippet}");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var snippet = respText.Length > 500 ? respText.Substring(0, 500) : respText;
                    throw new HttpRequestException(
                        $"视觉识别接口返回 {code} {resp.StatusCode}：{snippet}");
                }

                return respText;
            }
        }
    }

    /// <summary>
    /// 计算下一次重试前的等待时长：优先采用 429 响应头 <c>Retry-After</c>（相对秒数或绝对时间）；
    /// 无该头时按 2^attempt 指数退避。结果钳制在 1–120 秒，并尊重 <paramref name="ct"/> 以便用户取消。
    /// </summary>
    private static async Task BackoffAsync(int attempt, HttpResponseMessage? resp, CancellationToken ct)
    {
        int seconds;
        var ra = resp?.Headers.RetryAfter;
        if (ra != null)
        {
            if (ra.Delta.HasValue)
                seconds = (int)Math.Ceiling(ra.Delta.Value.TotalSeconds);
            else if (ra.Date.HasValue)
                seconds = Math.Max(0, (int)Math.Ceiling((ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds));
            else
                seconds = (int)Math.Pow(2, attempt);
        }
        else
        {
            seconds = (int)Math.Pow(2, attempt);
        }

        seconds = Math.Clamp(seconds, 1, 120);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 OpenAI 兼容响应体中抽取 message content。
    /// 若响应为错误对象（含 error 字段）则抛异常，便于调用方提示具体原因。
    /// </summary>
    public static string ExtractContent(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : (err.TryGetProperty("message", out var m) ? m.GetString() : null);
                throw new InvalidOperationException("视觉识别接口返回错误：" + (msg ?? err.GetRawText()));
            }

            return root
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("解析视觉识别响应失败：" + ex.Message, ex);
        }
    }

    /// <summary>从模型原始响应（或模型直接返回的 JSON 文本）中解析结构化结果。</summary>
    public static ImageAnalysisResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            int s = json.IndexOf('{');
            int e = json.LastIndexOf('}');
            if (s < 0 || e < 0 || e <= s) return null;
            var obj = json.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(obj);
            var root = doc.RootElement;
            return new ImageAnalysisResult
            {
                Category = Str(root, "category"),
                Scene = Str(root, "scene"),
                People = Str(root, "people"),
                Action = Str(root, "action"),
                Subtitle = Str(root, "subtitle"),
                Source = Str(root, "source"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }
}
