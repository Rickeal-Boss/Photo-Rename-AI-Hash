using System;
using System.Collections.Generic;
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
    // A-09：默认 HttpClient.Timeout 为 100s，叠加 15 次重试后单文件最坏等待 ≈28 分钟（服务端挂起时）。
    // 视觉接口正常响应 <20s，收紧到 60s 让挂起快速失败，重试语义与总时长可控。
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

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
            // C-04-5：400 对开启思考的模型（智谱 GLM-4.6V 系列等）会被思维链吃光导致 content 为空；
            // 官方建议 ≥1024，此处给足输出预算防 JSON 截断。
            max_tokens = 1024,
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

                // 429 里可能承载平台业务错误码：账户欠费/额度耗尽/套餐到期/无权限属永久错误，
                // 重试无意义（否则单文件会空转 15 次 ×15s 再被上层重排 10 次）。此处按业务码短路。
                // 仅对 429 生效，5xx 保持原有重试语义。
                if (code == 429 && IsPermanentBusinessCode(respText, out var bizCode))
                {
                    var permSnippet = respText.Length > 500 ? respText.Substring(0, 500) : respText;
                    throw new HttpRequestException(
                        $"视觉识别接口返回 429（业务错误码 {bizCode}：账户欠费 / 额度耗尽 / 套餐到期或无权限，重试无意义）：{permSnippet}",
                        null, resp.StatusCode);
                }

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
                    // P2-9：携带状态码（.NET 8 起 HttpRequestException.StatusCode 可用），调用方按状态码判定而非解析文案
                    throw new HttpRequestException(
                        $"视觉识别接口限流/错误（{code} {resp.StatusCode}），已尝试 {maxAttempts} 次仍失败：{snippet}", null, resp.StatusCode);
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var snippet = respText.Length > 500 ? respText.Substring(0, 500) : respText;
                    // P2-9：携带状态码，调用方按状态码判定而非解析文案
                    throw new HttpRequestException(
                        $"视觉识别接口返回 {code} {resp.StatusCode}：{snippet}", null, resp.StatusCode);
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
    /// 平台（如智谱）在 HTTP 429 中返回的「账户级永久错误」业务码：重试无法恢复，应立即失败。
    /// 参考智谱错误码表：1113 欠费、1308 已达使用上限、1309 套餐到期、1311 无该模型权限、
    /// 1315 Key 类型不匹配、1316~1321 各类周期/套餐上限。1302（并发超限）与 1305（平台过载）
    /// 属瞬时可恢复，<b>不在此集合内</b>，保持原有 15×15s 重试。
    /// </summary>
    private static readonly HashSet<string> PermanentBusinessCodes = new(StringComparer.Ordinal)
    {
        "1113", "1308", "1309", "1311", "1315", "1316", "1317", "1318", "1319", "1320", "1321",
    };

    /// <summary>尝试从响应体解析 error.code（字符串或数字皆可）；命中永久错误码时返回 true 并回传业务码。</summary>
    private static bool IsPermanentBusinessCode(string respText, out string bizCode)
    {
        bizCode = "";
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("code", out var c))
            {
                string v = c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : c.ToString();
                if (v.Length > 0 && PermanentBusinessCodes.Contains(v))
                {
                    bizCode = v;
                    return true;
                }
            }
        }
        catch
        {
            // 响应体非 JSON 或结构不符：按「非永久错误」处理，保持原有重试语义
        }
        return false;
    }

    /// <summary>
    /// 从 OpenAI 兼容响应体中抽取 message content，并兼容「思考型模型」的多种返回形态：
    /// content 为空时依次回退 <c>message.reasoning_content</c>（智谱 GLM-4.6V 系列命名）与
    /// <c>message.reasoning</c>（NVIDIA NIM 命名），随后剥离内联 &lt;think&gt; 段。
    /// 若正文为空且 <c>finish_reason == "length"</c>，说明输出预算被思维链耗尽，
    /// 抛出可归因的明确异常（而非让上层误判为「模型不按要求返回 JSON」）。
    /// 若响应为错误对象（含 error 字段）则抛异常，便于调用方提示具体原因。
    /// </summary>
    public static string ExtractContent(string raw)
    {
        string content;
        string finishReason = "";
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

            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");

            content = ReadMessageString(message, "content");
            if (string.IsNullOrWhiteSpace(content)) content = ReadMessageString(message, "reasoning_content");
            if (string.IsNullOrWhiteSpace(content)) content = ReadMessageString(message, "reasoning");

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString() ?? "";
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("解析视觉识别响应失败：" + ex.Message, ex);
        }

        var text = StripThink(content).Trim();
        if (text.Length == 0 && string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "视觉识别输出被 max_tokens 截断（finish_reason=length）且正文为空：模型可能开启了思考模式，" +
                "已耗尽输出预算。请关闭思考模式或提高最大输出 tokens 后重试。");

        return text;
    }

    /// <summary>安全读取 message 上的字符串字段；缺失或非字符串时返回空串。</summary>
    private static string ReadMessageString(JsonElement message, string key)
        => message.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";

    /// <summary>
    /// Nemotron reasoning 响应的内容抽取：行为已统一到 <see cref="ExtractContent"/>——
    /// 优先 <c>message.content</c>，为空回退 <c>message.reasoning</c>，并剥离内联 &lt;think&gt; 段。
    /// 保留本方法名以维持既有调用点不变。
    /// </summary>
    public static string ExtractNemotronContent(string raw) => ExtractContent(raw);

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
