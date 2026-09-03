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
    /// 调用视觉识别接口（默认请求体：文本 + 图片双段、temperature 0.3、max_tokens 400）。
    /// 失败时抛异常（而非静默返回 null），调用方据此提示具体原因。
    /// <paramref name="gate"/> 为可选限流闸门（如 NVIDIA 引擎的 40 RPM 闸门）：每次真实 HTTP
    /// 尝试（含重试）前先取名额；传入 null 即无限流（智谱/通义/自定义引擎行为不变）。
    /// 遇 HTTP 429（限流）或 5xx（服务端错误）时按固定 15 秒间隔自动重试，最多 15 次尝试
    /// （首试 + 至多 14 次重试），忽略 429 的 Retry-After 头；重试期间不返回任何结果，
    /// 因此调用方不会据此产出 <c>unknown_</c> 重命名；限流解除后继续。
    /// 4xx（非 429）为客户端永久错误，不重试、直接抛异常。
    /// </summary>
    /// <exception cref="InvalidOperationException">端点/模型/密钥为空，或网络/连通性异常（重试耗尽）。</exception>
    /// <exception cref="HttpRequestException">接口返回非成功状态码（携带状态码与响应体片段，重试耗尽）。</exception>
    public static Task<string> CallVisionApiAsync(
        string endpoint, string model, string apiKey, string prompt, string dataUrl, CancellationToken ct)
        => CallVisionApiAsync(endpoint, model, apiKey, prompt, dataUrl, gate: null, ct);

    /// <summary><see cref="CallVisionApiAsync(string,string,string,string,string,System.Threading.CancellationToken)"/>
    /// 的带限流闸门版本，其余行为完全一致（仅 NVIDIA 引擎传入闸门）。</summary>
    public static Task<string> CallVisionApiAsync(
        string endpoint, string model, string apiKey, string prompt, string dataUrl,
        RateGate? gate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("视觉识别模型名未配置（自定义引擎请在「设置」中填写模型名）。");

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

        return CallVisionApiRawAsync(endpoint, apiKey, JsonSerializer.Serialize(body), gate, ct);
    }

    /// <summary>
    /// 以调用方构造的 JSON 请求体直发 chat/completions（NVIDIA Nemotron 等需要自定义
    /// 请求体的引擎使用：如 reasoning 模型的 chat_template_kwargs 与更大 max_tokens）。
    /// 校验、重试、限流语义与 <see cref="CallVisionApiAsync(string,string,string,string,string,System.Threading.CancellationToken)"/>
    /// 完全一致；<paramref name="gate"/> 为每次真实 HTTP 尝试（含重试）前的可选限流闸门。
    /// </summary>
    public static async Task<string> CallVisionApiRawAsync(
        string endpoint, string apiKey, string jsonBody, RateGate? gate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("视觉识别端点 URL 未配置（自定义引擎请在「设置」中填写端点）。");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API Key 未配置：请在「设置」中填写所选识别引擎的 Key 后再开始整理。");

        // 安全：端点必须走 https，避免 API Key 与用户照片以明文 HTTP 出站（D-5）。
        // Zhipu/通义/NVIDIA 常量端点均为 https；此处主要约束用户自填的「自定义」端点。
        if (!endpoint.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "视觉识别端点必须使用 https（自定义引擎请在「设置」中填写以 https:// 开头的端点，避免 API Key 与照片以明文出站）。");

        // 429 限流 / 5xx 服务端错误：最多 15 次尝试（首试 + 至多 14 次重试），
        // 每次重试前固定等待 15 秒（忽略 429 的 Retry-After 头）；重试期间不返回任何结果，
        // 因此调用方不会据此产出 unknown_ 重命名；限流解除后继续。
        const int maxAttempts = 15;
        int attempt = 1;
        while (attempt <= maxAttempts)
        {
            ct.ThrowIfCancellationRequested();

            // 每次真实 HTTP 尝试都占用一个限流名额（重试也不例外），把速率压在窗口上限之下
            if (gate != null)
                await gate.WaitAsync(ct).ConfigureAwait(false);

            // HttpRequestMessage 单次使用，每次尝试都必须重新构造
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 网络/连通性异常：视为瞬时抖动可重试，耗尽后抛明确异常
                if (attempt < maxAttempts)
                {
                    await BackoffAsync(ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }
                throw new InvalidOperationException(
                    $"调用视觉识别接口失败（网络/连通性，已尝试 {maxAttempts} 次仍失败）：{ex.Message}。请检查网络与端点 URL 是否正确。", ex);
            }

            using (resp)
            {
                var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                int code = (int)resp.StatusCode;

                // 429 限流 / 5xx 服务端错误：固定等待 15 秒后重试，期间不做任何重命名
                if (code == 429 || code >= 500)
                {
                    if (attempt < maxAttempts)
                    {
                        await BackoffAsync(ct).ConfigureAwait(false);
                        attempt++;
                        continue;
                    }
                    var snippet = respText.Length > 500 ? respText.Substring(0, 500) : respText;
                    throw new HttpRequestException(
                        $"视觉识别接口限流/错误（{code} {resp.StatusCode}），已尝试 {maxAttempts} 次仍失败：{snippet}");
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

        // 兜底：循环内已保证在第 maxAttempts 次失败后抛出，此处仅满足编译器「所有路径均有返回值」要求
        throw new InvalidOperationException($"视觉识别接口调用超出最大尝试次数（{maxAttempts}）。");
    }

    /// <summary>
    /// 重试前的固定等待：15 秒（按需求固定间隔，忽略 429 的 Retry-After 头），并尊重 <paramref name="ct"/> 以便用户取消。
    /// </summary>
    private static async Task BackoffAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Nemotron reasoning 响应的内容抽取：优先 <c>choices[0].message.content</c>（最终答案）；
    /// 若 content 为空（如 thinking 未被请求体开关完全关闭）则回退 <c>message.reasoning</c>
    /// （官方 reasoning 字段，NIM 命名为 <c>reasoning</c> 而非 OpenAI 的 <c>reasoning_content</c>），
    /// 思维链中通常也夹带最终 JSON。同时剥离可能内联的 &lt;think&gt;…&lt;/think&gt; 段。
    /// 若响应为错误对象（含 error 字段）则抛异常，便于调用方提示具体原因。
    /// </summary>
    public static string ExtractNemotronContent(string raw)
    {
        string content;
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

            var message = root.GetProperty("choices")[0].GetProperty("message");
            content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(content) &&
                message.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
            {
                content = r.GetString() ?? "";
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("解析视觉识别响应失败：" + ex.Message, ex);
        }

        return StripThink(content).Trim();
    }

    /// <summary>剥离模型输出中可能内联的 &lt;think&gt;…&lt;/think&gt; 推理段（大小写不敏感）；
    /// 只有未闭合的 &lt;think&gt; 时把其后内容整体视为推理段丢弃（正常情况下不会出现：
    /// 本引擎已在请求体中显式关闭 thinking，此剥离仅作解析兜底）。</summary>
    private static string StripThink(string text)
    {
        var t = text;
        while (true)
        {
            int s = t.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (s < 0) return t;
            int e = t.IndexOf("</think>", s + 7, StringComparison.OrdinalIgnoreCase);
            t = e >= 0 ? t.Remove(s, e + 8 - s) : t.Substring(0, s);
        }
    }

    /// <summary>从模型原始响应（或模型直接返回的 JSON 文本）中解析结构化结果。
    /// 兼容模型偶发的格式瑕疵：markdown 代码围栏（```json … ```）、尾随逗号、行内注释。</summary>
    public static ImageAnalysisResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var obj = ExtractJsonObject(json);
        if (obj == null) return null;
        try
        {
            // 允许尾随逗号与注释：模型常见格式瑕疵，默认严格解析会直接判为「无法解析」
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            };
            using var doc = JsonDocument.Parse(obj, options);
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

    /// <summary>从可能夹带说明文字 / markdown 围栏的模型输出中，提取最外层的 JSON 对象文本。</summary>
    private static string? ExtractJsonObject(string json)
    {
        var text = json.Trim();

        // 剥离 markdown 代码围栏：```json … ``` 或 ``` … ```
        if (text.StartsWith("```"))
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0)
            {
                text = text.Substring(nl + 1);
                int fence = text.LastIndexOf("```");
                if (fence >= 0) text = text.Substring(0, fence);
                text = text.Trim();
            }
        }

        int s = text.IndexOf('{');
        int e = text.LastIndexOf('}');
        if (s < 0 || e < 0 || e <= s) return null;
        return text.Substring(s, e - s + 1);
    }

    private static string Str(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }
}
