using System;
using System.Diagnostics;
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
    // A-09：默认 HttpClient.Timeout 为 100s，叠加退避重试后单文件最坏等待会失控（服务端挂起时）。
    // 视觉接口正常响应 <20s，收紧到 60s 让挂起快速失败；再加上策略档的「最大 8 次 / 总预算 180s」
    // 双闸（见 AiProviderProfile.AiRetryPolicy），单文件最坏等待收敛到分钟级。
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<string?> EncodeAsJpegDataUrlAsync(string path, int maxDim, CancellationToken ct)
    {
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            var b64 = await ImageDecoder.EncodeResizedJpegAsync(fs, maxDim, ct).ConfigureAwait(false);
            return "data:image/jpeg;base64," + b64;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取消必须原样上抛：EncodeResizedJpegAsync 内部 ct 感知，取消时抛 OCE。
            // 此前被裸 catch 吞成 null，四个引擎都会报「无法解码图片」，归因完全错误
            // （写法与 Services/HashService.cs 的 TryComputeMd5Async 一致）。
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
    /// 调用视觉识别接口（默认请求体：文本 + 图片双段、temperature 0.3、max_tokens 1024）。
    /// 失败时抛异常（而非静默返回 null），调用方据此提示具体原因。
    /// 限流与退避语义全部由 <paramref name="profile"/> 决定（见 <see cref="AiProviderProfiles"/>）：
    /// 闸门按供应商（如 NVIDIA 30 RPM；智谱/通义/自定义默认无闸门），退避为指数退避
    /// （1s 起、×2、单次封顶按档位、带 0~1s jitter），并<b>遵从服务端 Retry-After / ratelimit-reset /
    /// JSON retryDelay 作为等待下限</b>（单次封顶 120s），整轮总耗时预算 180s。
    /// 遇 HTTP 429（限流）或 5xx（服务端错误）按上述策略重试；重试期间不返回任何结果，
    /// 因此调用方不会据此产出 <c>unknown_</c> 重命名；限流解除后继续。
    /// 4xx（非 429）为客户端永久错误，不重试、直接抛 <see cref="AiPermanentException"/>；
    /// 429 命中档位的永久错误规则（如智谱欠费/额度类业务码）同样判永久。
    /// </summary>
    /// <exception cref="InvalidOperationException">端点/模型/密钥为空，或网络/连通性异常（重试次数或退避预算耗尽）。</exception>
    /// <exception cref="HttpRequestException">429 限流 / 5xx 服务端错误重试次数或退避预算耗尽（携带状态码与响应体片段）。</exception>
    /// <exception cref="AiPermanentException">4xx 客户端永久错误（模型不存在 / 参数非法 / 401 / 403 / 404 等），或 429 命中永久业务规则，重试无意义。</exception>
    public static Task<string> CallVisionApiAsync(
        string endpoint, string model, string apiKey, string prompt, string dataUrl,
        AiProviderProfile profile, CancellationToken ct)
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

        return CallVisionApiRawAsync(endpoint, apiKey, JsonSerializer.Serialize(body), profile, ct);
    }

    /// <summary>
    /// 以调用方构造的 JSON 请求体直发 chat/completions（NVIDIA Nemotron 等需要自定义
    /// 请求体的引擎使用：如 reasoning 模型的 chat_template_kwargs 与更大 max_tokens）。
    /// 校验、重试、限流语义与 <see cref="CallVisionApiAsync"/> 完全一致，
    /// 均由 <paramref name="profile"/>（见 <see cref="AiProviderProfiles"/>）决定。
    /// </summary>
    public static async Task<string> CallVisionApiRawAsync(
        string endpoint, string apiKey, string jsonBody, AiProviderProfile profile, CancellationToken ct)
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

        // 429 限流 / 5xx 服务端错误：按供应商策略档退避重试（次数上限 + 总耗时预算双闸），
        // 并遵从服务端给出的重试时间提示；重试期间不返回任何结果，
        // 因此调用方不会据此产出 unknown_ 重命名；限流解除后继续。
        var pol = profile.Retry;
        var sw = Stopwatch.StartNew(); // 总耗时预算的计时基准（含 HTTP 往返与退避等待）
        int attempt = 1;
        while (attempt <= pol.MaxAttempts)
        {
            ct.ThrowIfCancellationRequested();

            // 每次真实 HTTP 尝试都占用一个限流名额（重试也不例外），把速率压在窗口上限之下
            if (profile.Gate != null)
                await profile.Gate.WaitAsync(ct).ConfigureAwait(false);

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
                // 网络/连通性异常：视为瞬时抖动可重试，次数或退避预算耗尽后抛明确异常。
                // 不抛 AiPermanentException：保留外层「单文件重排队」兜底（P17）。
                var netDelay = ComputeDelay(profile, attempt, null, null);
                if (attempt >= pol.MaxAttempts || sw.Elapsed + netDelay > pol.TotalBudget)
                {
                    // 必须区分「次数耗尽」与「退避预算耗尽」并打印累计秒数：
                    // 只说「限流/失败」会误导用户以为再等等就好（P23）。
                    var netWhy = attempt >= pol.MaxAttempts
                        ? $"已达最大尝试次数 {pol.MaxAttempts} 次"
                        : $"累计 {sw.Elapsed.TotalSeconds:F0}s 已耗尽退避预算 {pol.TotalBudget.TotalSeconds:F0}s";
                    throw new InvalidOperationException(
                        $"调用视觉识别接口失败（网络/连通性，已尝试 {attempt} 次 / 累计 {sw.Elapsed.TotalSeconds:F0}s，" +
                        $"{netWhy}）：{ex.Message}。请检查网络与端点 URL 是否正确。", ex);
                }

                await Task.Delay(netDelay, ct).ConfigureAwait(false);
                attempt++;
                continue;
            }

            using (resp)
            {
                var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                int code = (int)resp.StatusCode;

                // 429 里可能承载平台业务错误码：账户欠费/额度耗尽/套餐到期/无权限属永久错误，
                // 重试无意义（否则单文件会空转到退避预算耗尽再被上层重排 10 次）。此处按档位规则短路。
                // 仅对 429 生效，5xx 永不判永久（保持既有语义）。
                if (code == 429 && TryMatchPermanent(profile, respText, resp, out var why))
                {
                    // 抛专用类型而非 HttpRequestException：让 OrganizeService 能按「类型」判定永久错误并跳过
                    // 文件级重排队，同时不误伤「瞬时限流重试耗尽」（那也是 HttpRequestException(429)，
                    // 属用户已裁定的有意重试设计）。
                    throw new AiPermanentException(
                        $"视觉识别接口返回 429（{why}，重试无意义）：{Snippet(respText)}");
                }

                // 429 限流 / 5xx 服务端错误：按策略档退避后重试，期间不做任何重命名
                if (code == 429 || code >= 500)
                {
                    var delay = ComputeDelay(profile, attempt, resp, respText);
                    if (attempt >= pol.MaxAttempts || sw.Elapsed + delay > pol.TotalBudget)
                    {
                        // P2-9：携带状态码（.NET 8 起 HttpRequestException.StatusCode 可用），
                        // 调用方按状态码判定而非解析文案。异常文案必须区分「次数耗尽」与「退避预算耗尽」
                        // 并给出累计秒数——只说「限流」会误导用户以为再等等就好（P23）。
                        var why = attempt >= pol.MaxAttempts
                            ? $"已达最大尝试次数 {pol.MaxAttempts} 次"
                            : $"累计 {sw.Elapsed.TotalSeconds:F0}s 已耗尽退避预算 {pol.TotalBudget.TotalSeconds:F0}s";
                        throw new HttpRequestException(
                            $"视觉识别接口限流/错误（{code} {resp.StatusCode}），" +
                            $"已尝试 {attempt} 次 / 累计 {sw.Elapsed.TotalSeconds:F0}s，{why}：{Snippet(respText)}",
                            null, resp.StatusCode);
                    }

                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var snippet = Snippet(respText);
                    // 4xx（非 429）是客户端永久错误：模型不存在(1211)、参数非法(1214)、401/403/404 等，
                    // 重试永远不可能成功。此前抛 HttpRequestException，会被编排层重排队最多 10 次。
                    // 注意：本方法 XML 注释里早就写明「4xx 不重试」，但当时只体现在本方法的重试循环里，
                    // 没有通过异常类型传达给编排层——这里补上。
                    var hint = code == 403
                        ? "（403：若使用 NVIDIA NIM，该模型族可能尚未在你的账号下注册，请到 build.nvidia.com 对应模型页点击一次「Try API」）"
                        : "";
                    throw new AiPermanentException(
                        $"视觉识别接口返回 {code} {resp.StatusCode}（客户端永久错误，重试无意义）：{snippet}{hint}");
                }

                return respText;
            }
        }

        // 兜底：循环内已保证在最后一次失败后抛出，此处仅满足编译器「所有路径均有返回值」要求
        throw new InvalidOperationException($"视觉识别接口调用超出最大尝试次数（{pol.MaxAttempts}）。");
    }

    /// <summary>
    /// 计算本次失败后的等待时长：指数退避为基线，服务端提示（头 / JSON）只作<b>下限</b>——
    /// 服务端明确说「30s 后再来」时我们不会 1s 就重试，但它说「1s」时我们也不会放弃已积累的退避。
    /// 随后按 <see cref="AiRetryPolicy.RetryAfterCap"/> 封顶（防病态值冻死 UI），
    /// 最后加 0~1s 抖动（加法，不突破下限语义）。
    /// </summary>
    private static TimeSpan ComputeDelay(AiProviderProfile profile, int attempt, HttpResponseMessage? resp, string? respText)
    {
        var pol = profile.Retry;
        var delay = pol.DelayFor(attempt);

        if (pol.HonorRetryAfter && resp != null)
        {
            var now = DateTimeOffset.UtcNow;
            var hint = RetryAfterParser.FromHeaders(resp.Headers, profile.RetryAfterHeaders, now);
            if (hint == null && profile.RetryAfterFromJson) hint = RetryAfterParser.FromJson(respText, now);
            // Retry-After 是【下限】不是替代值：只在比当前退避更长时采用
            if (hint.HasValue && hint.Value > delay) delay = hint.Value;
        }

        if (delay > pol.RetryAfterCap) delay = pol.RetryAfterCap;
        if (pol.Jitter) delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        return delay;
    }

    /// <summary>
    /// 判定 429 是否其实是不可恢复的永久错误。求值顺序保守优先：<b>宁漏判（多退避几次）勿误判</b>——
    /// 误判会触发 OrganizeService 连续 3 次熔断中止整批（P17/P26）。
    /// </summary>
    private static bool TryMatchPermanent(AiProviderProfile profile, string respText, HttpResponseMessage resp, out string reason)
    {
        reason = "";
        var rules = profile.Permanent;

        // 1) error.code 精确匹配（字符串或数字皆可）：最强证据
        var bizCode = ReadErrorCode(respText);
        if (bizCode.Length > 0 && rules.BusinessCodes.TryGetValue(bizCode, out var desc))
        {
            reason = $"业务错误码 {bizCode}：{desc}";
            return true;
        }

        // 2) 响应体子串匹配：弱证据，默认空表（供应商不发结构化错误码时才启用）
        if (!string.IsNullOrEmpty(respText))
        {
            foreach (var kv in rules.BodySnippets)
            {
                if (respText.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reason = kv.Value;
                    return true;
                }
            }
        }

        // 3) 该供应商以「429 且不带任何重试提示」表达额度封顶（仅 Anthropic 档开启，默认关）
        if (rules.RetryAfterAbsentOn429IsPermanent)
        {
            var now = DateTimeOffset.UtcNow;
            var hint = RetryAfterParser.FromHeaders(resp.Headers, profile.RetryAfterHeaders, now);
            if (hint == null && profile.RetryAfterFromJson) hint = RetryAfterParser.FromJson(respText, now);
            if (hint == null)
            {
                reason = "响应未携带任何「多久后可重试」的提示（该供应商以此表达额度/预算封顶，重试不会恢复）";
                return true;
            }
        }

        // 4) 其余一律按可重试处理 → 走退避重试（P17 红线）
        return false;
    }

    /// <summary>从响应体解析 <c>error.code</c>（字符串或数字皆可）；解析不出返回空串。</summary>
    private static string ReadErrorCode(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("code", out var c))
            {
                return c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : c.ToString();
            }
        }
        catch
        {
            // 响应体非 JSON 或结构不符：按「无业务码」处理，不影响主流程
        }
        return "";
    }

    /// <summary>截断响应体用于异常文案（避免把整段 HTML/JSON 塞进 UI 日志）。</summary>
    private static string Snippet(string t) => t.Length > 500 ? t.Substring(0, 500) : t;

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
            // 该条件对同一模型配置是确定性的：调大 max_tokens 前重试多少次都一样，
            // 且失败结果不进 aiCache，每次重试都要重新付费。
            throw new AiPermanentException(
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
