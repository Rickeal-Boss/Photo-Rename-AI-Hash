using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// 把本地图片压成 JPEG data-URL。
    /// <para>失败时<b>抛 <see cref="AiPermanentException"/>(isBatchLevel: false)</b>（逐文件确定性失败，
    /// 不重试、不参与批次熔断），而非返回 null——异常携带原始 inner 与脱敏后的真因，
    /// 避免四个引擎把它统一翻译成「图片已损坏」而误导排查方向（P1-6 / P23）。</para>
    /// </summary>
    /// <exception cref="OperationCanceledException">用户取消（原样上抛，不得吞成解码失败）。</exception>
    public static async Task<string> EncodeAsJpegDataUrlAsync(string path, int maxDim, CancellationToken ct)
    {
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            var b64 = await ImageDecoder.EncodeResizedJpegAsync(fs, maxDim, ct).ConfigureAwait(false);
            return "data:image/jpeg;base64," + b64;
        }
        catch (OperationCanceledException)
        {
            // 取消必须原样上抛：EncodeResizedJpegAsync 内部 ct 感知，取消时抛 OCE。
            // 此前被裸 catch 吞成 null，四个引擎都会报「无法解码图片」，归因完全错误
            // （写法与 Services/HashService.cs 的 TryComputeMd5Async 一致）。
            throw;
        }
        catch (Exception ex)
        {
            // P1-6：此前这里把 ex 整个丢弃、返回 null，四个引擎统一翻译成「图片已损坏」——
            // 但这条通道承载的真实原因至少 5 类：文件损坏 / 缺少编解码器（HEIC）/ 被其它进程独占 /
            // 超 256MB 输入上限（ImageDecoder）/ 像素缓冲尺寸自检失败（ImageDecoder）。
            // 归因必然错误（P23）：用户拿着「已损坏」的提示去查一张其实被云盘锁住或超大的图。
            // 改为抛带 inner 的 AiPermanentException：真因以脱敏文案带上（crash.log 可定位），
            // 并保持「逐文件不重试、不熔断整批」的既有分级（isBatchLevel: false）。
            throw new AiPermanentException(
                "无法解码图片（可能不是有效图像 / 已损坏 / 缺少编解码器（如 HEIC）/ 被其它程序占用 / 尺寸超限）：" +
                $"{System.IO.Path.GetFileName(path)}。（{Snippet(ex.Message ?? "")}）",
                isBatchLevel: false,
                inner: ex);
        }
    }

    /// <summary>命名模板中「由 AI 产出」的占位符。只有模板用到它们时，识别结果才会进入文件名。</summary>
    /// <remarks>与 <c>OrganizeService</c> 的指纹判定共用同一份定义，避免两处各写一份导致漂移。</remarks>
    internal static readonly string[] AiPlaceholders =
        { "{category}", "{scene}", "{people}", "{action}", "{subtitle}", "{source}" };

    /// <summary>
    /// 按命名模板算出「本次真正需要 AI 输出哪些字段」。
    /// </summary>
    /// <returns>
    /// 模板用到的 AI 字段（按固定顺序）；模板为空或不含任何 AI 占位符时返回<b>全部</b>字段——
    /// 后者用于「模板未知」的兜底（例如直接调用本方法做单次识别），保证行为不退化。
    /// </returns>
    private static string[] RequiredAiKeys(string? template)
    {
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(template))
        {
            foreach (var p in AiPlaceholders)
            {
                if (template!.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // 占位符 {category} → 字段名 category
                keys.Add(p.Trim('{', '}'));
            }
        }
        // 模板为空 / 一个 AI 占位符都没用 → 兜底为全部字段
        return keys.Count > 0 ? keys.ToArray() : new[] { "category", "scene", "people", "action", "subtitle", "source" };
    }

    /// <summary>
    /// 构造识图提示词。
    /// </summary>
    /// <param name="language">界面语言（zh-CN / en-US），决定 AI 输出字段的语言。</param>
    /// <param name="template">
    /// 用户的命名模板。<b>强烈建议传入</b>：只有模板真正用到的字段才会被要求输出，
    /// 既省 token，也让模型把注意力集中在会用到的字段上（未用到的字段不必生成，出错面也更小）。
    /// 传 null 则按全部字段兜底。
    /// </param>
    /// <remarks>
    /// 三条关键经验（都是实测踩出来的）：
    /// 1. <b>示例比指令更有约束力</b>：示例必须与目标语言一致，否则模型会照抄示例语言 → 中英混合。
    /// 2. <b>只问要用的</b>：要求模型输出用不到的字段，既浪费 token 又引入不必要的出错点。
    /// 3. <b>取值要有边界</b>：给词表、给长度上限、给非法字符约束，比"简短描述"这类模糊要求准确得多，
    ///    因为文件名字段最终要落进文件名（有长度与字符集限制）。
    /// </remarks>
    public static string BuildPrompt(string language, string? template = null)
    {
        bool zh = (language ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string[] keys = RequiredAiKeys(template);

        // 各字段的取值说明（只在需要时纳入）。给候选词表 + 长度上限 + 非法字符约束，
        // 比"简短描述"这类模糊要求准确得多——这些值最终要拼进文件名。
        string Spec(string key) => key switch
        {
            "category" => "category=main subject type; pick ONE from " +
                          (zh ? "美食/宠物/人物/风景/建筑/文档/截图/商品/车辆/其它" : "food/pet/people/landscape/building/document/screenshot/product/vehicle/other") + " if possible",
            "scene" => "scene=where the photo was taken; a short noun phrase",
            "people" => "people=who appears; use 'none' if none or unidentifiable; NEVER invent names",
            "action" => "action=what is happening; a short verb phrase",
            "subtitle" => "subtitle=visible on-screen text, kept VERBATIM in its original language; 'none' if none",
            "source" => "source=origin token; pick ONE from " +
                        (zh ? "手机/相机/截图/电脑/生成" : "phone/camera/screenshot/computer/generated"),
            _ => ""
        };

        // 语言规则：category/scene/people/action 统一为所选语言；
        // subtitle 是画面上「看到」的文字，天然多语言，按原样保留、不要翻译；
        // source 沿用通用标识，也不做语言统一。
        string langRule = zh
            ? "Language: category/scene/people/action MUST be Simplified Chinese only (no English words " +
              "except unavoidable proper nouns); subtitle keeps original text, do NOT translate; " +
              "source uses the tokens above. Never mix Chinese and English in one result."
            : "Language: category/scene/people/action MUST be English only; subtitle keeps original text, " +
              "do NOT translate; source uses the tokens above. Never mix languages in one result.";

        // 示例只包含本次真正需要的字段，且与目标语言一致。
        string zhSample = "{\"category\":\"宠物\",\"scene\":\"客厅\",\"people\":\"无\",\"action\":\"睡觉\",\"subtitle\":\"none\",\"source\":\"手机\"}";
        string enSample = "{\"category\":\"pet\",\"scene\":\"living room\",\"people\":\"none\",\"action\":\"sleeping\",\"subtitle\":\"none\",\"source\":\"phone\"}";
        string ordered = string.Join(",", keys);
        // 示例按本次需要的字段裁剪，保持与要求完全一致的形状（键顺序也要一致，模型会照抄形状）。
        var sampleParts = new List<string>();
        foreach (var k in keys)
        {
            sampleParts.Add(zh switch
            {
                true => k switch
                {
                    "category" => "\"category\":\"宠物\"",
                    "scene" => "\"scene\":\"客厅\"",
                    "people" => "\"people\":\"无\"",
                    "action" => "\"action\":\"睡觉\"",
                    "subtitle" => "\"subtitle\":\"none\"",
                    "source" => "\"source\":\"手机\"",
                    _ => ""
                },
                _ => k switch
                {
                    "category" => "\"category\":\"pet\"",
                    "scene" => "\"scene\":\"living room\"",
                    "people" => "\"people\":\"none\"",
                    "action" => "\"action\":\"sleeping\"",
                    "subtitle" => "\"subtitle\":\"none\"",
                    "source" => "\"source\":\"phone\"",
                    _ => ""
                }
            });
        }
        string example = "{" + string.Join(",", sampleParts) + "}";

        var sb = new StringBuilder();
        sb.Append("Analyze this image and return ONLY a compact JSON object. No explanation, no markdown fence. ");
        sb.Append("Keys (in this order): ").Append(ordered).Append(". ");
        foreach (var k in keys) sb.Append(Spec(k)).Append(". ");
        sb.Append("Rules: every value <= 12 characters; use 'none' when unknown; ");
        sb.Append("never use characters / \\ : * ? \" < > | or newlines. ");
        sb.Append(langRule).Append(" ");
        sb.Append("Example: ").Append(example);
        return sb.ToString();
    }

    /// <summary>
    /// 调用视觉识别接口（默认请求体：文本 + 图片双段、temperature 0.3、max_tokens 1024）。
    /// 失败时抛异常（而非静默返回 null），调用方据此提示具体原因。
    /// 限流与退避语义全部由 <paramref name="profile"/> 决定（见 <see cref="AiProviderProfiles"/>）：
    /// 闸门按供应商（如 NVIDIA 30 RPM；智谱/通义/自定义默认无闸门），退避为指数退避
    /// （1s 起、×2、单次封顶按档位、带 0~1s jitter），并<b>遵从服务端 Retry-After / ratelimit-reset /
    /// JSON retryDelay 作为等待下限</b>（单次封顶 120s），整轮退避等待预算 180s
    /// （<b>口径：只约束「等待」，不含最后一次 HTTP 请求本身的耗时</b>——HttpClient.Timeout = 60s，
    /// 故单文件墙钟最坏 ≈ 180s 等待 + 60s 请求 ≈ 240s+，不要对外承诺「总耗时 180s」）。
    /// 遇 HTTP 429（限流）、5xx（服务端错误）、408（请求超时）、425（过早）按上述策略重试；
    /// 重试期间不返回任何结果，
    /// 因此调用方不会据此产出 <c>unknown_</c> 重命名；限流解除后继续。
    /// 其余 4xx 为客户端永久错误，不重试、直接抛 <see cref="AiPermanentException"/>；
    /// 429 命中档位的永久错误规则（如智谱欠费/额度类业务码）同样判永久。
    /// </summary>
    /// <param name="delayAsync">可选的「退避等待」替换钩子：null（默认）时内部走 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>；
    /// 非 null 时改由调用方实现等待——用于让「暂停」能打断退避等待（组织层暂停检查点夹在 AI 调用前后，
    /// 退避期间点暂停原本要等约 4 分钟才生效，期间还在发请求计费）。
    /// <b>契约必须与 Task.Delay 一致</b>：等满传入的时长后返回，且 <c>ct</c> 取消时抛
    /// <see cref="OperationCanceledException"/>，否则本方法的取消/暂停语义会错乱。</param>
    /// <exception cref="AiPermanentException">端点 / 模型 / 密钥为空，或端点不是 https（<b>配置类</b>，
    /// 对整批成立、重试无意义：不进文件级重排队，连续 3 个文件命中即中止整批并提示用户去设置页补全）。</exception>
    /// <exception cref="InvalidOperationException">循环兜底：调用超出最大尝试次数（理论不可达）。</exception>
    /// <exception cref="AiTransientException">网络 / 连通性异常，或服务端 60 秒未响应（超时），且重试次数或退避预算已耗尽
    /// —— 属瞬时故障，换时间点重试有真实成功率（原始异常保留在 <c>InnerException</c>）。</exception>
    /// <exception cref="HttpRequestException">429 限流 / 5xx / 408 / 425 服务端错误重试次数或退避预算耗尽（携带状态码与响应体片段）。</exception>
    /// <exception cref="AiPermanentException">4xx 客户端永久错误（模型不存在 / 参数非法 / 401 / 403 / 404 等），或 429 命中永久业务规则，重试无意义。</exception>
    public static Task<string> CallVisionApiAsync(
        string endpoint, string model, string apiKey, string prompt, string dataUrl,
        AiProviderProfile profile, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        // 配置缺失 = 整批级永久错误：模型名为空时每个文件都会以完全相同的方式失败，
        // 此前抛裸 InvalidOperationException 会被编排层判成「确定性失败」再重排队 2 次——
        // 空配置重试没有任何成功可能，只是浪费 2 次请求并推迟「请填写模型名」的提示。
        // isBatchLevel: true 必须显式写出（P26：不可依赖默认值）。
        if (string.IsNullOrWhiteSpace(model))
            throw new AiPermanentException(
                "视觉识别模型名未配置（自定义引擎请在「设置」中填写模型名）。",
                isBatchLevel: true);

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

        // delayAsync 必须透传：本方法只是构造默认请求体的包装，
        // 漏传会让「暂停可打断退避」在走 CallVisionApiAsync 的引擎（自定义 / 通义）上静默失效。
        return CallVisionApiRawAsync(endpoint, apiKey, JsonSerializer.Serialize(body), profile, ct, delayAsync);
    }

    /// <summary>
    /// 以调用方构造的 JSON 请求体直发 chat/completions（NVIDIA Nemotron 等需要自定义
    /// 请求体的引擎使用：如 reasoning 模型的 chat_template_kwargs 与更大 max_tokens）。
    /// 校验、重试、限流语义与 <see cref="CallVisionApiAsync"/> 完全一致，
    /// 均由 <paramref name="profile"/>（见 <see cref="AiProviderProfiles"/>）决定。
    /// </summary>
    /// <param name="delayAsync">同 <see cref="CallVisionApiAsync"/> 的同名参数：可选的退避等待替换钩子。</param>
    public static async Task<string> CallVisionApiRawAsync(
        string endpoint, string apiKey, string jsonBody, AiProviderProfile profile, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        // 以下三处与 CallVisionApiAsync 的「模型名为空」同属配置缺失：
        // 整批级永久错误，不重排队（空配置重试无成功可能），连续 3 个文件命中即中止整批。
        // 中文文案逐字保持不变，只换异常类型——用户看到的仍是「请到设置页填写 …」的可操作提示。
        // isBatchLevel: true 一律显式写出（P26：不可依赖 AiPermanentException 的默认值）。
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new AiPermanentException(
                "视觉识别端点 URL 未配置（自定义引擎请在「设置」中填写端点）。",
                isBatchLevel: true);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiPermanentException(
                "API Key 未配置：请在「设置」中填写所选识别引擎的 Key 后再开始整理。",
                isBatchLevel: true);

        // 安全：端点必须走 https，避免 API Key 与用户照片以明文 HTTP 出站（D-5）。
        // Zhipu/通义/NVIDIA 常量端点均为 https；此处主要约束用户自填的「自定义」端点。
        if (!endpoint.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new AiPermanentException(
                "视觉识别端点必须使用 https（自定义引擎请在「设置」中填写以 https:// 开头的端点，避免 API Key 与照片以明文出站）。",
                isBatchLevel: true);

        // 429 限流 / 5xx 服务端错误：按供应商策略档退避重试（次数上限 + 总耗时预算双闸），
        // 并遵从服务端给出的重试时间提示；重试期间不返回任何结果，
        // 因此调用方不会据此产出 unknown_ 重命名；限流解除后继续。
        var pol = profile.Retry;
        // 总耗时预算的计时基准。注意：闸门等待（profile.Gate.WaitAsync）也计入这 180s 预算，
        // 这是有意为之——用户手填 rpmLimit=1 时门等 60s 会吃掉预算、只剩 2~3 次尝试，
        // 但这正是「每分钟 1 次」应有的慢；把门等排除在预算外反而会让总时长不可控。
        var sw = Stopwatch.StartNew();
        int attempt = 1;
        while (attempt <= pol.MaxAttempts)
        {
            ct.ThrowIfCancellationRequested();

            // 每次真实 HTTP 尝试都占用一个限流名额（重试也不例外），把速率压在窗口上限之下
            if (profile.Gate != null)
                await profile.Gate.WaitAsync(ct).ConfigureAwait(false);

            // HttpRequestMessage 单次使用，每次尝试都必须重新构造。
            // 构造期的配置类异常（端点 URL 非法 / Key 含非法字符）已由 CreateRequest 归入
            // AiPermanentException(isBatchLevel: true)：此前这三行在 try 之外，UriFormatException /
            // ArgumentException（消息可能回显 Key 片段）会以框架异常逃逸，绕过既有的异常分类体系（P1-1）。
            using var req = CreateRequest(endpoint, apiKey, jsonBody);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户主动取消：必须原样上抛，绝不能被下面的「网络/连通性」分支包装成
                // InvalidOperationException——那会让取消看起来像故障、并被编排层重排队。
                // 写法与同文件 EncodeAsJpegDataUrlAsync 的 OperationCanceledException 过滤器保持一致。
                throw;
            }
            catch (Exception ex)
            {
                // 网络/连通性异常：视为瞬时抖动可重试，次数或退避预算耗尽后抛明确异常。
                // 不抛 AiPermanentException：保留外层「单文件重排队」兜底（P17）。
                // 抛 AiTransientException（而非裸 InvalidOperationException）：见下方 throw 处的说明。
                ct.ThrowIfCancellationRequested(); // 取消优先于一切：不让取消被包装成业务异常

                var netDelay = ComputeDelay(profile, attempt, null, null);
                if (attempt >= pol.MaxAttempts || sw.Elapsed + netDelay > pol.TotalBudget)
                {
                    // HttpClient 的 60s 超时抛的是 TaskCanceledException（InnerException 为 TimeoutException），
                    // 与「用户取消」同型 —— 必须按 InnerException 区分，否则归因完全错误（P23）：
                    // 超时该说「服务端 60 秒未响应」，而不是让用户去检查网络与端点。
                    var isTimeout = ex is TaskCanceledException tce && tce.InnerException is TimeoutException;
                    var kind = isTimeout ? "服务端 60 秒未响应（超时）" : "网络/连通性";
                    var tail = isTimeout
                        ? "请稍后重试，或到「设置」更换响应更快的引擎 / 端点。"
                        : "请检查网络与端点 URL 是否正确。";
                    // 必须区分「次数耗尽」与「退避预算耗尽」并给出可执行的信息：
                    // 只说「限流/失败」会误导用户以为再等等就好（P23）。
                    var netWhy = attempt >= pol.MaxAttempts
                        ? $"已达最大尝试次数 {pol.MaxAttempts} 次"
                        : $"下一次需再等 {netDelay.TotalSeconds:F0}s，累计将达 {(sw.Elapsed + netDelay).TotalSeconds:F0}s，" +
                          $"超出退避预算 {pol.TotalBudget.TotalSeconds:F0}s（已耗 {sw.Elapsed.TotalSeconds:F0}s）";
                    // 抛专用类型而非裸 InvalidOperationException：本分支是「网络 / 连通性」与「60 秒超时」
                    // 两类**瞬时故障**，按用户已裁定的口径应保留「单文件 10 次重排队」；
                    // 而裸 InvalidOperationException 与「配置缺失 / 解析失败」同型，编排层只能靠 inner 链反推
                    // （HttpClient 抛出的原始异常类型不稳定，IOException / SocketException 会被漏判成确定性失败）。
                    // inner 保持原样：既有 inner 链判据继续成立（双重保险）。
                    // P1-5：文案里的 ex.Message 必须过 Snippet（截断 + 脱敏）——底层异常消息在部分场景会携带
                    // 请求 URI（含 ?key= / ?api_key=，用户把 Key 拼进自定义端点 URL 是现实用法）或代理回显的认证头，
                    // 而该文案会经 crash.log / rename_log.csv 落盘（D-5）。
                    throw new AiTransientException(
                        $"调用视觉识别接口失败（{kind}，已尝试 {attempt} 次 / 累计 {sw.Elapsed.TotalSeconds:F0}s，" +
                        $"{netWhy}）：{Snippet(ex.Message ?? "")}。{tail}", ex);
                }

                await DelayAsync(delayAsync, netDelay, ct).ConfigureAwait(false);
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
                // 注意：此处 out var 写在 if 条件里，变量作用域会泄漏到外层 using 块（C# 7 语义），
                // 故取专用名 permWhy —— 与下面限流分支的 rateWhy 区分，避免 CS0136 撞名。
                if (code == 429 && TryMatchPermanent(profile, respText, resp, out var permWhy))
                {
                    // 抛专用类型而非 HttpRequestException：让 OrganizeService 能按「类型」判定永久错误并跳过
                    // 文件级重排队，同时不误伤「瞬时限流重试耗尽」（那也是 HttpRequestException(429)，
                    // 属用户已裁定的有意重试设计）。
                    throw new AiPermanentException(
                        $"视觉识别接口返回 429（{permWhy}，重试无意义）：{Snippet(respText)}",
                        // P1-G：显式写出而非依赖默认值 true。权衡：命中永久业务码（欠费/额度/套餐）
                        // 与启发式规则 3（429 且无重试提示）都对整批成立，故确实应为 true；
                        // 但默认值有「被将来新增的逐文件规则静默纳入」的风险（P26），必须显式声明。
                        isBatchLevel: true);
                }

                // 429 限流 / 5xx 服务端错误 / 408 请求超时 / 425 过早：按策略档退避后重试，期间不做任何重命名。
                // 408 / 425 在此显式并入可重试集合（此前随「其余 4xx」被判整批级永久错误、一次都不重试）：
                // - 408 Request Timeout：RFC 9110 明确「客户端可以重发请求」，服务端只是没等到/主动放弃；
                // - 425 Too Early：用于防重放，语义是「换个时间再来」。
                // 两者都不是配置类错误，改成可重试才符合本项目「宁可慢也不产出 unknown_」的既定口径。
                if (code == 429 || code == 408 || code == 425 || code >= 500)
                {
                    var delay = ComputeDelay(profile, attempt, resp, respText);
                    if (attempt >= pol.MaxAttempts || sw.Elapsed + delay > pol.TotalBudget)
                    {
                        // P2-9：携带状态码（.NET 8 起 HttpRequestException.StatusCode 可用），
                        // 调用方按状态码判定而非解析文案。异常文案必须区分「次数耗尽」与「退避预算耗尽」
                        // 并给出累计秒数——只说「限流」会误导用户以为再等等就好（P23）。
                        // 预算耗尽的判定是「已耗 + 下一次等待 > 预算」，所以已耗必定 < 预算：
                        // 文案必须说清「下一次再等 X 秒就会达到 Y 秒、超出预算」，否则会出现
                        // 「累计 154s 已耗尽退避预算 180s」这种自相矛盾、无法归因的输出（P23）。
                        var rateWhy = attempt >= pol.MaxAttempts
                            ? $"已达最大尝试次数 {pol.MaxAttempts} 次"
                            : $"下一次需再等 {delay.TotalSeconds:F0}s，累计将达 {(sw.Elapsed + delay).TotalSeconds:F0}s，" +
                              $"超出退避预算 {pol.TotalBudget.TotalSeconds:F0}s（已耗 {sw.Elapsed.TotalSeconds:F0}s）";
                        throw new HttpRequestException(
                            $"视觉识别接口限流/错误（{code} {resp.StatusCode}），" +
                            $"已尝试 {attempt} 次 / 累计 {sw.Elapsed.TotalSeconds:F0}s，{rateWhy}：{Snippet(respText)}",
                            null, resp.StatusCode);
                    }

                    await DelayAsync(delayAsync, delay, ct).ConfigureAwait(false);
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
                    // isBatchLevel 必须按状态码显式分级（见 IsBatchLevel4xx）：
                    // 不能依赖 AiPermanentException 的默认 true —— 那会把逐文件的 413/415
                    // 也纳入「连续 3 次熔断中止整批」（P26 三层口径）。
                    throw new AiPermanentException(
                        $"视觉识别接口返回 {code} {resp.StatusCode}（客户端永久错误，重试无意义）：{snippet}{hint}",
                        isBatchLevel: IsBatchLevel4xx(code, respText));
                }

                return respText;
            }
        }

        // 兜底：循环内已保证在最后一次失败后抛出，此处仅满足编译器「所有路径均有返回值」要求
        throw new InvalidOperationException($"视觉识别接口调用超出最大尝试次数（{pol.MaxAttempts}）。");
    }

    /// <summary>
    /// 构造单次 HTTP 请求。端点 URL 非法（如用户只填了 <c>https://</c>）或 Key 含不能放进 HTTP 头的
    /// 字符（换行 / 控制字符 / 非 ASCII / 非 token 字符，常见于从终端整行粘贴）时，
    /// <see cref="HttpRequestMessage"/> / <see cref="AuthenticationHeaderValue"/> 会抛框架异常
    /// （<c>UriFormatException</c> / <c>ArgumentException</c>，且后者消息可能回显 Key 片段）。
    /// 这些是<b>配置类错误</b>（对整批成立、重试无意义），必须归入既有异常体系：
    /// 统一转 <see cref="AiPermanentException"/>(isBatchLevel: true)，让编排层「连续 3 个文件命中即中止整批
    /// 并提示去设置页」，而不是让它以框架异常逃逸、被判成确定性失败（重排 2 次）并抛出英文文案（P1-1）。
    /// </summary>
    private static HttpRequestMessage CreateRequest(string endpoint, string apiKey, string jsonBody)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return req;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 文案过 Snippet：框架异常消息可能回显 Key 片段，异常会经 crash.log / rename_log.csv 落盘（D-5）。
            throw new AiPermanentException(
                "视觉识别请求构造失败：端点 URL 或 API Key 格式不合法（可能含换行 / 非法字符），" +
                $"请到「设置」检查端点与 Key。（{Snippet(ex.Message ?? "")}）",
                isBatchLevel: true);
        }
    }

    /// <summary>
    /// 退避等待的唯一出口：<paramref name="delayAsync"/> 为 null 时就是 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>，
    /// 行为与改造前<b>逐字节一致</b>（默认 null → 所有既有调用点不受影响）。
    /// 非 null 时改走调用方实现（目前用途：让「暂停」能打断退避等待——组织层的两个暂停检查点
    /// 夹在 AI 调用前后，退避期间点暂停原本要等满约 4 分钟才生效，期间仍在发请求计费）。
    /// 注意 C# 语法：async 方法里不能用 ref / out（本项目踩过 CS1988），故「可替换等待」只能用委托参数表达。
    /// </summary>
    private static Task DelayAsync(Func<TimeSpan, CancellationToken, Task>? delayAsync, TimeSpan delay, CancellationToken ct)
    {
        if (delayAsync == null) return Task.Delay(delay, ct);
        // 委托返回 null 会让 await 抛 NullReferenceException → 落到组织层通用 catch → 一次重试都没做就失败
        // 并被重排队 10 次（P18 静默失败）。此处回退到 Task.Delay 兜底。
        return delayAsync(delay, ct) ?? Task.Delay(delay, ct);
    }

    /// <summary>
    /// 计算本次失败后的等待时长：指数退避为基线，服务端提示（头 / JSON）只作<b>下限</b>——
    /// 服务端明确说「30s 后再来」时我们不会 1s 就重试，但它说「1s」时我们也不会放弃已积累的退避。
    /// 随后按 <see cref="AiRetryPolicy.RetryAfterCap"/> 封顶（防病态值冻死 UI），
    /// 最后加 0~1s 抖动（加法，不突破「服务端提示是下限」的语义）。
    /// 注意：jitter 加在封顶之后，故实际等待允许超出 <c>RetryAfterCap</c> 至多 1s —— 这是有意的，
    /// 不是 bug：封顶针对的是服务端给的病态值，抖动则是为了打散多客户端同步重试。
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

        // 2) error.details.error_code 精确匹配（Anthropic 风格的结构化字段）：同样是强证据。
        //    必须排在下面的「429 且无 retry-after」启发式规则之前：结构化字段是官方明文的判据，
        //    启发式只是「官方保证普通限流一定带 retry-after」前提下的兜底，可靠性低一档。
        var detailCode = ReadDetailErrorCode(respText);
        if (detailCode.Length > 0 && rules.DetailErrorCodes.TryGetValue(detailCode, out var detailDesc))
        {
            reason = $"错误码 {detailCode}：{detailDesc}";
            return true;
        }

        // 3) 响应体子串匹配：弱证据，默认空表（供应商不发结构化错误码时才启用）
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

        // 4) 兜底启发式：该供应商以「429 且不带任何重试提示」表达额度封顶（仅 Anthropic 档开启，默认关）。
        //    Anthropic 官方明确普通速率限流一定带 retry-after，故本规则对其安全；
        //    但它是推断而非证据，仅在上述结构化判据都没命中时才走到这里。
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

        // 5) 其余一律按可重试处理 → 走退避重试（P17 红线）
        return false;
    }

    /// <summary>
    /// 从响应体解析 <c>error.details.error_code</c>；解析不出返回空串。
    /// <c>error.details</c> 可能是<b>对象</b>也可能是<b>数组</b>（各供应商写法不一），两种形态都要容错；
    /// 解析失败一律返回空串，不影响主流程（与 <see cref="ReadErrorCode"/> 同口径）。
    /// </summary>
    private static string ReadDetailErrorCode(string respText)
    {
        // 与 ReadErrorCode 同口径：不为找结构化字段去全量解析一个大响应体
        if (string.IsNullOrEmpty(respText) || respText.Length > MaxCodeScanLength) return "";

        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (!doc.RootElement.TryGetProperty("error", out var err)) return "";
            if (!err.TryGetProperty("details", out var details)) return "";

            if (details.ValueKind == JsonValueKind.Object)
                return ReadStringProperty(details, "error_code");

            if (details.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in details.EnumerateArray())
                {
                    var v = ReadStringProperty(item, "error_code");
                    if (v.Length > 0) return v;
                }
            }
        }
        catch
        {
            // 响应体非 JSON 或结构不符：按「无此字段」处理，不影响主流程
        }
        return "";
    }

    /// <summary>安全读取 JSON 对象上的字符串字段；元素不是对象、字段缺失或非字符串时返回空串。</summary>
    private static string ReadStringProperty(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(key, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";

    /// <summary>
    /// 4xx 永久错误是否「对整批成立」（决定是否参与 OrganizeService 的连续 3 次熔断）。
    /// 按 P26 三层口径分级：<b>不能一律用 AiPermanentException 的默认 true</b>——
    /// 单张图过大 / 格式不被接受时服务端回 413 / 415，而相机连拍的大图在目录里连号，
    /// 连续 3 张极易达成，会把「跳过 3 张」升级成「整批失败」，文案还指向账户/额度，完全误导。
    /// <para>前置条件：408（请求超时）与 425（过早）已在调用处被分流进「可重试」分支，
    /// <b>不会走到本方法</b>；若将来有人把它们移回 4xx 分流，必须同步在本方法里按 false 处理，
    /// 否则「超时」会被读成账户/额度问题（P26 三层口径）。</para>
    /// </summary>
    private static bool IsBatchLevel4xx(int code, string respText)
    {
        // 载荷过大 / 媒体类型不被接受：只针对当前文件 → 逐文件，不熔断
        if (code == 413 || code == 415) return false;

        // 鉴权 / 权限 / 端点不存在 / 方法不允许：配置级错误，对整批成立
        if (code == 401 || code == 403 || code == 404 || code == 405) return true;

        // 400 / 422 要区分「配置类」与「逐文件类」：
        // 智谱的「1211 模型不存在」「1214 参数非法」返回的是 HTTP 400 而不是 429，
        // 走不到 429 业务码分支，必须在这里按 error.code 白名单显式区分。
        if (code == 400 || code == 422)
        {
            var biz = ReadErrorCode(respText);
            return biz == "1211" || biz == "1214";
        }

        // 其余 4xx 无证据表明是逐文件问题 → 保守按整批级
        return true;
    }

    /// <summary>从响应体解析 <c>error.code</c>（字符串或数字皆可）；解析不出返回空串。</summary>
    private static string ReadErrorCode(string respText)
    {
        // 与 RetryAfterParser.FromJson 同口径：每次 429/4xx 都会走到这里，
        // 不为找业务码去全量解析一个大响应体（代理错误页、几 MB 的报错 JSON）
        if (string.IsNullOrEmpty(respText) || respText.Length > MaxCodeScanLength) return "";

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

    /// <summary>响应体扫描上限（与 <c>RetryAfterParser.MaxJsonScanLength</c> 同口径）。</summary>
    private const int MaxCodeScanLength = 65536;

    /// <summary>
    /// 截断响应体用于异常文案：<b>先截断再脱敏</b>（顺序不可反，否则正则要扫全量响应体）。
    /// 脱敏是必须的（D-5）：企业代理的错误页模板（如 Squid）会回显完整请求 URL（含 ?key=），
    /// 主流网关也可能在错误文案里回显 Key 片段。异常文案虽不落盘，但用户截图外发即泄露。
    /// </summary>
    private static string Snippet(string t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        var s = t.Length > 500 ? t.Substring(0, 500) : t;

        // 顺序不可反：先截断（见上），再对这 500 字符脱敏
        s = Regex.Replace(s, @"(?i)(bearer\s+)[A-Za-z0-9._\-]{8,}", "$1***");
        // P1-4 缺口 1：分隔符必须允许「空格」——「API key: xxx」「Access Token: xxx」带空格是英文文案里
        // 最常见的写法，原 `api[_-]?key` 只认 `_`/`-`/无分隔，导致该规则完全漏网（当时只剩四家前缀兜底）。
        s = Regex.Replace(s, @"(?i)((?:api[\s_\-]?key|apikey|access[\s_\-]?token|secret)\s*[:=]\s*""?)[A-Za-z0-9._\-]{8,}", "$1***");
        // 「&amp;」是 HTML 转义的「&」：企业代理的错误页模板（Squid 等）会把查询串转义后回显，
        // 只认裸 & 会让 ?key= 的脱敏整条失效（漏一圈等于没脱敏）。
        s = Regex.Replace(s, @"(?i)([?&](?:amp;)?(?:key|api[_-]?key|access_token|token)=)[^&\s""]+", "$1***");

        // 以下两条补「无关键字邻接的裸 Key 回显」（如 "invalid key sk-proj-xxxx"、路径内嵌 /v1/sk-xxx）：
        // 上面三条都依赖关键字（bearer / apikey / ?key=）邻接，厂商直接把 Key 拼进错误文案时全部漏网。
        // 下限取 16 而非 8：显式前缀虽强，但 "task-oriented" / "risk-management" 这类正常英文单词也含 "sk-"，
        // 8 字符下限会误伤（误伤虽不致错，却会让错误文案被 *** 打碎到无法阅读）；真实 Key 长度远大于 16。
        // 前缀用 \b 界定：避免 "task-…" 里的 "sk-" 被当成 OpenAI 前缀。
        // P1-4 缺口 2：前缀表补 nvapi —— NVIDIA 是本项目四个内置引擎之一，其 Key 形如 "nvapi-8nQ…"，
        // 原表只有 sk/gsk/xai，一旦任何错误文案回显 nvapi- 开头的 Key 就是完整明文泄露。
        s = Regex.Replace(s, @"(?i)(\b(?:sk|gsk|xai|nvapi)[-_])[A-Za-z0-9._\-]{16,}", "$1***");
        // Google / Gemini 的 Key 形如 "AIzaSy…"：AIza 后面直接跟字符、无分隔符，故本条不要求分隔符
        s = Regex.Replace(s, @"(\bAIza)[A-Za-z0-9._\-]{16,}", "$1***");
        return s;
    }

    /// <summary>
    /// 从 OpenAI 兼容响应体中抽取 message content，并兼容「思考型模型」的多种返回形态：
    /// content 为空时依次回退 <c>message.reasoning_content</c>（智谱 GLM-4.6V 系列命名）与
    /// <c>message.reasoning</c>（NVIDIA NIM 命名），随后剥离内联 &lt;think&gt; 段。
    /// 若 <c>finish_reason == "length"</c>（输出被 max_tokens 截断）且解析不出结构化结果
    /// —— 含正文为空与「留下半截 JSON」两种形态 —— 说明输出预算被思维链耗尽，
    /// 抛 <see cref="AiPermanentException"/> 短路（而非让上层误判为「模型不按要求返回 JSON」后重排队 10 次）；
    /// 截断但 JSON 恰好完整时正常返回，不受影响。
    /// 若响应为错误对象（含 error 字段）则抛异常，便于调用方提示具体原因（文案已过 <c>Snippet</c> 脱敏）。
    /// 若正文为空且非截断（模型什么都没产出），抛 <see cref="AiPermanentException"/>(isBatchLevel: false)。
    /// </summary>
    /// <exception cref="InvalidOperationException">响应体含 error 字段（该分支保留既有异常类型，文案已脱敏）。</exception>
    /// <exception cref="AiResultInvalidException">响应体不是合法 JSON、不是 JSON 对象，或结构不符（重试有意义）。</exception>
    /// <exception cref="AiPermanentException">输出被 max_tokens 截断且解析不出结果（整批级，不重试）；
    /// 或正文为空且非截断（逐文件级，不重试、不熔断整批）。</exception>
    public static string ExtractContent(string raw)
    {
        string content = "";
        string finishReason = "";
        // 服务端 error 分支的脱敏文案：在 try 内收集、try 外抛出——避免 try 内抛异常被下方的
        // catch (Exception) 再包一层（P1-2：把「放行 error 分支」从过宽的 catch 过滤器里拆出来）。
        string? serverError = null;
        // 根元素不是 JSON 对象（网关返回 "OK" / [1,2] / 42 / null / true 等合法但非对象的 JSON）
        bool notJsonObject = false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // P1-2：根不是对象时 TryGetProperty 会抛 InvalidOperationException —— 必须在此显式判型，
            // 交给下方统一按「重试有意义」的 AiResultInvalidException 处理；否则它会以框架异常逃逸，
            // 被编排层判成确定性失败（重排队从 10 次降到 2 次），与既定口径相反。
            if (root.ValueKind != JsonValueKind.Object)
            {
                notJsonObject = true;
            }
            else if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    // err 不是对象时（如 {"error":42} / {"error":[...]}）不能调 TryGetProperty——那会抛
                    // InvalidOperationException 并被下方 catch 误判成「响应结构不符」。此处显式判型，
                    // 非对象一律回落到 Snippet(err.GetRawText())（与 ReadErrorCode / ReadDetailErrorCode 同口径）。
                    : (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() : null);
                // P0-3：error.message 是服务端原文，聚合层/自建网关会把完整 Key 回显在其中
                //（如 "Incorrect API key provided: sk-…"），而本异常会经 crash.log / rename_log.csv 落盘。
                // 两个分支都必须过 Snippet（截断 + 脱敏）——原写法只对 ?? 右侧生效，msg 原样进异常（唯一一条绕过 Snippet 的响应体外泄通道）。
                serverError = "视觉识别接口返回错误：" +
                              (msg is { Length: > 0 } ? Snippet(msg) : Snippet(err.GetRawText()));
            }
            else
            {
                var choice = root.GetProperty("choices")[0];
                var message = choice.GetProperty("message");

                content = ReadMessageString(message, "content");
                if (string.IsNullOrWhiteSpace(content)) content = ReadMessageString(message, "reasoning_content");
                if (string.IsNullOrWhiteSpace(content)) content = ReadMessageString(message, "reasoning");

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finishReason = fr.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            // 用专用类型而非裸 InvalidOperationException：让编排层能把「响应体不是合法 JSON / 结构不符」
            // （网关偶发返回 HTML 错误页或异常结构，重试有意义）与「配置缺失 / 端点错误」分开计重排队次数。
            // 继承 InvalidOperationException，既有 catch 行为完全不变（纯增量）。
            // inner 必须保留：编排层靠 inner 链识别网络 / 超时。
            // 注意：不再保留 catch (InvalidOperationException) { throw; } 过滤器 —— 它会把
            // 「root 非对象」「choices 存在但非数组」等 System.Text.Json 误用异常一并原样放行（P1-2），
            // 使本该是 AiResultInvalidException 的解析失败拿不到正确类型、分档判据失效。
            throw new AiResultInvalidException("解析视觉识别响应失败：" + ex.Message, ex);
        }

        // error 分支：保留既有异常类型 InvalidOperationException（不改变其分档语义），但文案已在上面过 Snippet。
        if (serverError != null) throw new InvalidOperationException(serverError);
        if (notJsonObject)
            throw new AiResultInvalidException("视觉识别响应不是 JSON 对象（网关可能返回了非预期的标量 / 数组）：" + Snippet(raw));

        var text = StripThink(content).Trim();

        // finish_reason == "length" 表示输出被 max_tokens 截断。此前只在「正文为空」时判永久，
        // 但更常见的形态是「留下半截 JSON」：正文非空 → 本方法照常返回 → 四个引擎的 Parse 返回 null →
        // 抛普通 InvalidOperationException → OrganizeService 对同一张图重排队 10 次，
        // 每次都用同样的 max_tokens 重发、结果高度确定，却付费 10 次。
        // 故此处补一条精确判据：截断 + 解析不出结构化结果 = 确定性失败，直接短路为整批级永久错误。
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) && Parse(text) == null)
        {
            // 只在截断分支里才调 Parse：正常响应不付这份解析开销，行为也与改造前逐字节一致。
            // 「截断但 JSON 恰好完整」的正常结果在此被保留——Parse 成功即返回文本，不短路。
            // 该条件对同一模型配置是确定性的：调大 max_tokens 前重试多少次都一样，
            // 且失败结果不进 aiCache，每次重试都要重新付费。
            //
            // 已知残留（刻意不为它增加复杂度）：Parse 取的是「首个 { 到最后一个 }」的贪心片段，
            // 若模型在完整 JSON 之后又吐了一段自带 '}' 的内容，贪心片段非法 → 会走到本分支误判永久。
            // 触发概率极低（提示词已要求「只输出一个 JSON 对象」），且旧行为同样是失败后重排队 10 次
            // （同样不成功、同样付费 10 次），故收益不值得引入一段括号配平扫描。
            throw new AiPermanentException(
                "视觉识别输出被 max_tokens 截断（finish_reason=length）" +
                (text.Length == 0 ? "且正文为空" : "，返回的是不完整的 JSON，无法解析") +
                "：模型可能开启了思考模式，已耗尽输出预算。" +
                "请关闭思考模式或提高最大输出 tokens 后重试。",
                // P1-G：显式写出而非依赖 AiPermanentException 的默认值 true。权衡：截断源于 max_tokens
                // 与模型配置（同一批所有文件同源），故确实应为 true；但默认值有「被将来新增的逐文件
                // 规则静默纳入」的风险（P26），必须显式声明——与下面 429 永久分支同一口径。
                isBatchLevel: true);
        }

        // P1-3：正文为空且不是 max_tokens 截断 → 模型什么都没产出（常见 finish_reason=stop / content_filter / 缺失）。
        // 这种形态重采样成功率≈0（content_filter 对同一张图更是确定性的），若按「解析不出」判可重试，
        // 单文件会重排 10 次、每次重发整张图 = 10 倍无效计费（单文件最多 10×8=80 次付费请求换一个必然为空的结果）。
        // 故判为不可重试的逐文件永久错误：
        //  - isBatchLevel: false —— 内容审核拒绝是逐图的，不得升级成「连续 3 个文件熔断整批」（P26 三层口径）；
        //  - 与上面的 finish_reason=="length" 分支对齐（那一支已用更具体的文案短路，故此处只处理非截断形态）；
        //  - 网络类瞬时故障不走这里（它们在 CallVisionApiRawAsync 内已按 AiTransientException 处理，未改动）。
        if (text.Length == 0)
        {
            throw new AiPermanentException(
                "视觉识别返回了空内容（模型未产出任何文本，可能因内容审核被拒绝）：" +
                "请更换识别模型或检查图片。",
                isBatchLevel: false);
        }

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
    /// 兼容模型偶发的格式瑕疵：markdown 代码围栏（```json … ```）、尾随逗号、行内注释。
    /// <para><b>P0-2（模板感知）：本次命名真正依赖的 AI 字段「一个都没拿到」时返回 null（判为解析失败）</b>，
    /// 而不是返回一个「关键字段全空」的对象当成功——后者会让编排层继续走完，把空字段替换成 <c>unknown</c>，
    /// 落地 <c>unknown_…</c> 垃圾名、记为成功、并在续传指纹里被永久锁定（永不重做）。</para>
    /// <para>判据的松紧经过权衡：
    /// <list type="bullet">
    /// <item><description><b>不取「任一字段为空」</b>：AI 本来就可能只填一部分字段，那属正常，误判会带来 10 倍无效计费。</description></item>
    /// <item><description><b>不取「六个字段全空」</b>：模板只要 <c>{category}</c> 而模型只回了 <c>{"scene":…}</c> 时，
    /// 六字段里有非空值，旧判据会放行 → 照样产出 <c>unknown_…</c>（这是用户更容易撞到的形态）。</description></item>
    /// <item><description><b>取「模板真正用到的字段全空」</b>：只有本次命名真正依赖的字段一个都没拿到，才算结果不可用。</description></item>
    /// </list></para>
    /// <para><paramref name="template"/> 为 null / 空 / 不含 AI 占位符时，<see cref="RequiredAiKeys"/> 兜底返回全部 6 个字段，
    /// 判据自然退化为「六字段全空」（保持既有行为不退化；<see cref="ExtractContent"/> 的截断探测即走此路）。</para>
    /// <para>返回 null 后由调用方（四个引擎的 <c>result ?? throw new AiResultInvalidException(...)</c>）接管，
    /// 走「重试有意义」的 10 次档——<b>绝不能改成永久错误</b>，否则连续 3 个文件会熔断整批（P26）。</para>
    /// </summary>
    /// <param name="template">命名模板：决定「哪些字段算本次命名必需」。null 时按全部 6 个字段兜底。</param>
    public static ImageAnalysisResult? Parse(string? json, string? template = null)
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
            var result = new ImageAnalysisResult
            {
                Category = Str(root, "category"),
                Scene = Str(root, "scene"),
                People = Str(root, "people"),
                Action = Str(root, "action"),
                Subtitle = Str(root, "subtitle"),
                Source = Str(root, "source"),
            };

            // P0-2：模板真正用到的 AI 字段「全部为空」→ 判解析失败（返回 null）。
            // 典型触发形态（全部是合法 JSON，故上面不会抛、result 非 null）：包装层 {"result":{…}} / {"data":{…}}、
            // 键名本地化 {"分类":…}、键名大小写漂移 {"Category":…}、值不是字符串 {"category":1}、空对象 {}，
            // 以及「模型只回了模板用不到的字段」（如模板要 category，模型只给 scene）。
            // 模板未知（null/空/无 AI 占位符）时 RequiredAiKeys 兜底为全部 6 个字段，判据退化为「六字段全空」。
            bool anyRequired = false;
            foreach (var key in RequiredAiKeys(template))
            {
                if (!string.IsNullOrWhiteSpace(FieldOf(result, key))) { anyRequired = true; break; }
            }
            if (!anyRequired) return null;

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按字段名取 <see cref="ImageAnalysisResult"/> 上的值。
    /// 键名取自 <see cref="AiPlaceholders"/> 去掉花括号后的固定小写名（见 <see cref="RequiredAiKeys"/>）。</summary>
    private static string FieldOf(ImageAnalysisResult r, string key) => key switch
    {
        "category" => r.Category,
        "scene" => r.Scene,
        "people" => r.People,
        "action" => r.Action,
        "subtitle" => r.Subtitle,
        "source" => r.Source,
        _ => "",
    };

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
