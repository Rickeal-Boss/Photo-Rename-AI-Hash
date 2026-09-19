using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// 单个供应商的「退避策略」：指数退避 + 服务端提示 + 总预算上限三部分。
/// 取值依据：
/// <list type="bullet">
/// <item><description><c>MaxDelay = 60s</c>：对齐 OpenAI 官方 SDK 的 <c>wait_random_exponential(max=60)</c>，
/// 单次退避不无限增长。</description></item>
/// <item><description><c>RetryAfterCap = 120s</c>：服务端给的 Retry-After 只作下限、不盲从；
/// 封顶防止服务端给出 1 小时这类病态值把 UI 冻死。</description></item>
/// <item><description><c>TotalBudget = 180s</c> / <c>MaxAttempts = 8</c>：折中档（用户拍板）。
/// 取代旧的「15 次 × 固定 15s ≈ 3.75 分钟盲等」——旧实现既不看服务端提示，也没有总预算，
/// 服务端长时间限流时单文件会空转到最坏 15×(15s+60s)。</description></item>
/// </list>
/// </summary>
public sealed class AiRetryPolicy
{
    /// <summary>最大尝试次数（含首试）。</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>首次退避时长。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>退避增长因子（指数退避）。</summary>
    public double Factor { get; init; } = 2.0;

    /// <summary>单次退避封顶（不含服务端提示加成后的 jitter）。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>是否叠加 0~1s 随机抖动：多文件/多客户端同时被限流时避免同步重试（「惊群」）。</summary>
    public bool Jitter { get; init; } = true;

    /// <summary>是否遵从服务端给出的重试时间提示（Retry-After / ratelimit-reset / JSON retryDelay）。</summary>
    public bool HonorRetryAfter { get; init; } = true;

    /// <summary>服务端提示可接受的上限：超过即按本值等待（防服务端给出小时级值冻死 UI）。</summary>
    public TimeSpan RetryAfterCap { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>本轮（单文件/单次调用）重试的总耗时预算：累计耗时 + 下一次等待超过预算即放弃等待并抛出。</summary>
    public TimeSpan TotalBudget { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>第 <paramref name="attempt"/> 次尝试失败后的退避时长（attempt 从 1 开始）：
    /// <c>min(InitialDelay × Factor^(attempt-1), MaxDelay)</c>。</summary>
    public TimeSpan DelayFor(int attempt)
    {
        int n = attempt < 1 ? 0 : attempt - 1;
        var seconds = InitialDelay.TotalSeconds * Math.Pow(Factor, n);
        if (seconds > MaxDelay.TotalSeconds) seconds = MaxDelay.TotalSeconds;
        if (seconds < 0) seconds = 0;
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// 「可重试状态码（429）里哪些其实是不可恢复的永久错误」的判定规则。
/// 保守优先：宁可漏判（多退避几次），不可误判（触发 OrganizeService 连续 3 次熔断中止整批，P17/P26）。
/// </summary>
public sealed class AiPermanentRuleSet
{
    /// <summary>响应体 <c>error.code</c>（字符串或数字皆可）→ 中文说明。命中即判永久（强证据）。</summary>
    public Dictionary<string, string> BusinessCodes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 响应体 <c>error.details.error_code</c> → 中文说明。命中即判永久（强证据）。
    /// 与 <see cref="BusinessCodes"/> 区分：后者读顶层 <c>error.code</c>（智谱 / 阿里风格），
    /// 本表读嵌套的 <c>error.details.error_code</c>（Anthropic 风格）。
    /// 判定优先级：<see cref="BusinessCodes"/> ≈ 本表 ＞ <see cref="BodySnippets"/> ＞
    /// <see cref="RetryAfterAbsentOn429IsPermanent"/>（启发式兜底）。
    /// </summary>
    public Dictionary<string, string> DetailErrorCodes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>响应体子串 → 中文说明。命中即判永久（弱证据，默认空表：纯文本匹配容易误伤）。</summary>
    public Dictionary<string, string> BodySnippets { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 为 true 时：429 且响应里<b>解析不出任何重试时间提示</b>即判永久。
    /// 这是 Anthropic 花费上限（spend cap）的表现特征——它不发 Retry-After，只回 429。
    /// <b>默认关闭且只在 host 命中 api.anthropic.com 时开启</b>：智谱 / 阿里 / NVIDIA / SiliconFlow
    /// 同样不发 429 提示头，若全局开启会把它们的瞬时限流全部误判成永久，
    /// 进而触发 OrganizeService 连续 3 次熔断中止整批（P17/P26 红线）。
    /// Anthropic 官方明确「普通速率限流一定带 retry-after」，故此规则对 Anthropic 是安全的；
    /// 但 <see cref="DetailErrorCodes"/>（enforced_spend_limit_reached）是官方明文的结构化判据，
    /// 更可靠 —— <b>本规则已降级为兜底</b>，结构化字段优先（见 ImageAnalysisHelper.TryMatchPermanent）。
    /// </summary>
    public bool RetryAfterAbsentOn429IsPermanent { get; init; }
}

/// <summary>把一个 AI 供应商的「限流闸门 + 退避策略 + 永久错误规则」打包成一份不可变配置。</summary>
public sealed class AiProviderProfile
{
    /// <summary>档位名（仅用于日志/诊断）。</summary>
    public string Name { get; init; } = "Generic";

    /// <summary>可选限流闸门：每次真实 HTTP 尝试（含重试）前取名额；null 表示不主动限速。</summary>
    public RateGate? Gate { get; init; }

    /// <summary>退避策略。</summary>
    public AiRetryPolicy Retry { get; init; } = new();

    /// <summary>本供应商在响应头里承载「多久后可重试」的头名列表（按序尝试，首个解析成功者生效）。
    /// 默认值直接引用 <see cref="RetryAfterParser.StandardHeaders"/> 这一<b>共享只读数组</b>：
    /// 多个档位共用同一实例，禁止写入元素，要改请整体替换。</summary>
    public string[] RetryAfterHeaders { get; init; } = RetryAfterParser.StandardHeaders;

    /// <summary>是否额外从响应体 JSON 解析重试提示（Gemini 的 <c>error.details[].retryDelay</c> 等）。</summary>
    public bool RetryAfterFromJson { get; init; } = true;

    /// <summary>该供应商的永久错误判定规则。</summary>
    public AiPermanentRuleSet Permanent { get; init; } = new();
}

/// <summary>
/// 「多久后可重试」的解析器。各供应商写法五花八门，统一收口在这里：
/// <list type="bullet">
/// <item><description>秒数（整数或小数）：<c>29</c> / <c>29.5</c></description></item>
/// <item><description>毫秒后缀：<c>300ms</c>（OpenAI 系 <c>retry-after-ms</c> 头则是纯数字毫秒）</description></item>
/// <item><description>时长表达式：<c>29s</c> / <c>6m0s</c> / <c>2m59.56s</c>（Gemini / Anthropic）</description></item>
/// <item><description>RFC3339 绝对时刻：<c>2026-09-19T12:00:00Z</c>（Anthropic 的 reset 头）</description></item>
/// </list>
/// <b>解析失败一律返回 null（静默失败）</b>：宁可少遵从一次提示走纯退避，也不可被病态值（如 1 小时）冻死 UI。
/// </summary>
public static class RetryAfterParser
{
    /// <summary>OpenAI 系 / 多数网关使用的标准头名。大小写不敏感（HTTP 头本就如此，此处两写仅为可读性）。
    /// <b>只读共享数组</b>（被多个策略档的 <see cref="AiProviderProfile.RetryAfterHeaders"/> 共同引用）：
    /// 数组元素<b>禁止写入</b>，要改请整体替换引用。</summary>
    public static readonly string[] StandardHeaders =
    {
        "retry-after-ms",
        "Retry-After",
        "retry-after",
        "x-ratelimit-reset-requests",
        "x-ratelimit-reset-tokens",
        "x-ratelimit-reset",
    };

    /// <summary>Anthropic 专有头名（各维度分别给出「什么时候恢复」，值为 RFC3339 时刻）。
    /// 同 <see cref="StandardHeaders"/>：<b>只读共享数组，禁止写入元素</b>。</summary>
    public static readonly string[] AnthropicHeaders =
    {
        "anthropic-ratelimit-requests-reset",
        "anthropic-ratelimit-tokens-reset",
        "anthropic-ratelimit-input-tokens-reset",
        "anthropic-ratelimit-output-tokens-reset",
    };

    /// <summary>提示值可信区间上限：超过 1 小时一律视为解析失败（静默失败防线）。</summary>
    private static readonly TimeSpan MaxPlausibleHint = TimeSpan.FromHours(1);

    /// <summary>JSON 体扫描上限（P2-5）：每次 429/5xx 都会调 <see cref="FromJson"/>，
    /// 不设限会对大响应体（整页 HTML 错误页、几 MB 的报错 JSON）做一次全量 <c>JsonDocument.Parse</c>，
    /// 而重试提示一定在响应头部几 KB 内。超限即放弃 JSON 解析，退回纯退避。</summary>
    private const int MaxJsonScanLength = 65536;

    /// <summary>时长表达式：<c>1h30m</c> / <c>6m0s</c> / <c>2m59.56s</c> / <c>29s</c>，三段均可选但至少一段非空。</summary>
    private static readonly Regex DurationPattern = new(
        @"^(?:(?<h>\d+(?:\.\d+)?)h)?(?:(?<m>\d+(?:\.\d+)?)m)?(?:(?<s>\d+(?:\.\d+)?)s)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 按 <paramref name="names"/> 依次在响应头里找重试提示，返回首个解析成功的值；全部失败返回 null。
    /// </summary>
    public static TimeSpan? FromHeaders(HttpHeaders headers, string[] names, DateTimeOffset now)
    {
        if (headers == null || names == null) return null;

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!headers.TryGetValues(name, out var values)) continue;

            // 头名是 retry-after-ms 时，值是「毫秒数」而非秒数
            var isMilliseconds = string.Equals(name, "retry-after-ms", StringComparison.OrdinalIgnoreCase);
            foreach (var raw in values)
            {
                if (TryParse(raw, now, out var delay, isMilliseconds)) return delay;
            }
        }
        return null;
    }

    /// <summary>
    /// 从响应体 JSON 里解析重试提示：Gemini 的 <c>error.details[].retryDelay</c>、
    /// 通用的 <c>error.retryDelay</c> 与顶层 <c>RetryInfo.retryDelay</c>。失败返回 null。
    /// </summary>
    public static TimeSpan? FromJson(string? body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        if (body.Length > MaxJsonScanLength) return null; // P2-5：见 MaxJsonScanLength 注释

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var direct = ReadRetryDelay(err, now);
                if (direct.HasValue) return direct;

                // Gemini 把重试提示放在 error.details[]（google.rpc.RetryInfo）里
                if (err.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in details.EnumerateArray())
                    {
                        var d = ReadRetryDelay(item, now);
                        if (d.HasValue) return d;
                    }
                }
            }

            // 顶层 RetryInfo.retryDelay
            if (root.TryGetProperty("RetryInfo", out var retryInfo))
            {
                var d = ReadRetryDelay(retryInfo, now);
                if (d.HasValue) return d;
            }
        }
        catch
        {
            // 响应体非 JSON / 结构不符：按「没有提示」处理，交给纯退避，不影响主流程
        }
        return null;
    }

    /// <summary>
    /// 解析单个「多久后可重试」文本。解析次序不可调整（见实现内注释）。
    /// <b>契约：解析不出或值病态一律返回 false，绝不抛异常。</b>
    /// 调用方 <c>ImageAnalysisHelper.ComputeDelay</c> 外围没有 try，异常一旦逃逸会直接落到
    /// OrganizeService 的通用 catch —— 一次重试都没做就失败、再重排队 10 次（P18 静默失败 + P23 归因错误）。
    /// </summary>
    /// <param name="isMilliseconds">true 表示 <paramref name="raw"/> 是毫秒数（头名 retry-after-ms）。</param>
    public static bool TryParse(string? raw, DateTimeOffset now, out TimeSpan delay, bool isMilliseconds = false)
    {
        delay = TimeSpan.Zero;
        // 整体兜底：与 FromJson 的静默失败口径一致（P40）
        try
        {
            return TryParseCore(raw, now, out delay, isMilliseconds);
        }
        catch
        {
            delay = TimeSpan.Zero;
            return false;
        }
    }

    private static bool TryParseCore(string? raw, DateTimeOffset now, out TimeSpan delay, bool isMilliseconds = false)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();

        // 1) 头名是 retry-after-ms：纯数字毫秒
        if (isMilliseconds)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return false;
            if (!IsPlausible(ms, MaxPlausibleHint.TotalMilliseconds)) return false; // 必须先挡，见 IsPlausible 注释
            return Positive(TimeSpan.FromMilliseconds(ms), out delay);
        }

        // 2) 纯数字 → 秒。用 double 而非 int：服务端可能给 "29.5" 这类小数
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (!IsPlausible(seconds, MaxPlausibleHint.TotalSeconds)) return false;
            return Positive(TimeSpan.FromSeconds(seconds), out delay);
        }

        // 3) "30ms" 后缀：必须排在正则之前——否则 "30ms" 会被正则的 m 组吃成 30 分钟、
        //    剩下的 "s" 前面没有数字 → 整串不匹配，提示被白白丢掉
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(s.Substring(0, s.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms2))
                return false;
            if (!IsPlausible(ms2, MaxPlausibleHint.TotalMilliseconds)) return false;
            return Positive(TimeSpan.FromMilliseconds(ms2), out delay);
        }

        // 4) 时长表达式："29s" / "6m0s" / "2m59.56s" / "1h30m"
        var match = DurationPattern.Match(s);
        if (match.Success)
        {
            double total = 0;
            bool any = false;
            if (TryReadGroup(match, "h", out var h)) { total += h * 3600; any = true; }
            if (TryReadGroup(match, "m", out var m)) { total += m * 60; any = true; }
            if (TryReadGroup(match, "s", out var sec)) { total += sec; any = true; }
            if (any)
            {
                // 分组值本身可以大到离谱（如 "999999999999s"），同样要先挡
                if (!IsPlausible(total, MaxPlausibleHint.TotalSeconds)) return false;
                return Positive(TimeSpan.FromSeconds(total), out delay);
            }
        }

        // 5) 绝对时刻（Anthropic 的 reset 头用 RFC3339）：换算为相对现在的时长。
        //    顺带覆盖 HTTP-date（RFC1123，如 "Wed, 21 Oct 2015 07:28:00 GMT"）——
        //    DateTimeOffset.TryParse 能识别 GMT 后缀，不是「解析失败」。
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            var until = at - now;
            // 时钟偏移加固（P1-G）：Anthropic 的 reset 头是绝对时刻，若本机时钟快于服务端
            // 超过重置窗口（常见 60s，未做 NTP 同步即可能），until 会为负。此时若判「解析失败」，
            // 会被 Anthropic 档的规则 3（429 且无任何重试提示 = 额度封顶）误判成永久错误
            // → 连续 3 次熔断中止整批（P26）。中间网关剥离该头同理。
            // 故「已过期」一律钳到 1 秒（语义：立刻可以重试），让调用方拿到非空 hint。
            if (until <= TimeSpan.Zero)
            {
                delay = TimeSpan.FromSeconds(1);
                return true;
            }
            return Positive(until, out delay);
        }

        // 6) 其余病态值（无法归入以上任何一种格式）：解析失败
        return false;
    }

    /// <summary>
    /// 数值可用性守卫：<b>必须在构造 TimeSpan 之前</b>调用。
    /// <c>TimeSpan.FromSeconds</c> / <c>FromMilliseconds</c> 对 NaN 抛 ArgumentException、
    /// 对 ±∞ 与超范围值抛 OverflowException —— 而 <see cref="Positive"/> 守卫在它们之后才执行，
    /// 防线装在异常之后等于没有防线（P40）。
    /// 真实触发场景：① <c>x-ratelimit-reset</c> 头返回 Unix epoch 秒（如 1699999999，
    /// GitHub Models / Azure 风格端点常见，而该头就在 <see cref="StandardHeaders"/> 里）；
    /// ② <c>1e999</c> —— .NET Core 3.0+ 的 double.TryParse 溢出返回 ±∞ 而非 false；
    /// ③ <c>NaN</c>。
    /// 上界取 <paramref name="plausibleMax"/>×2：给 Positive 留出「超过 1 小时即不可信」的判定空间。
    /// </summary>
    private static bool IsPlausible(double value, double plausibleMax)
        => double.IsFinite(value) && Math.Abs(value) <= plausibleMax * 2;

    /// <summary>读取正则命名分组的数值；分组未参与匹配时返回 false。</summary>
    private static bool TryReadGroup(Match match, string name, out double value)
    {
        value = 0;
        var g = match.Groups[name];
        if (!g.Success || g.Length == 0) return false;
        return double.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>从 JSON 对象里读 <c>retryDelay</c>（字符串或数字皆可）；读不到返回 null。</summary>
    private static TimeSpan? ReadRetryDelay(JsonElement element, DateTimeOffset now)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("retryDelay", out var v)) return null;

        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return TryParse(s, now, out var delay) ? delay : (TimeSpan?)null;
    }

    /// <summary>
    /// 守卫：只有「正数且不超过 1 小时」的提示才被采信。
    /// 这是静默失败防线——宁可少遵从一次提示，不可被病态值冻死 UI。
    /// </summary>
    private static bool Positive(TimeSpan t, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (t <= TimeSpan.Zero) return false;
        if (t > MaxPlausibleHint) return false;
        delay = t;
        return true;
    }
}

/// <summary>
/// 各 AI 供应商的策略档注册表。<see cref="ImageAnalysisHelper"/> 的所有退避 / 限速 / 永久错误判定
/// 都由这里提供，避免在调用层散落 if-else——新增供应商只需在此加一份档位。
/// </summary>
public static class AiProviderProfiles
{
    /// <summary>
    /// NVIDIA 闸门：60 秒 30 次（原 40）。
    /// 依据：40 RPM 仅有论坛佐证，官方文档没有速率限制章节；而本应用串行调用下实际只有 6~10 RPM
    /// （单张图片含压缩 + 网络往返），从 40 降到 30 的降速成本≈0，等于白拿 25% 余量保险。
    /// 进程级共享：同一 key 的额度本就是跨批次连续计费的。
    /// </summary>
    private static readonly RateGate NvidiaGate = new(30, TimeSpan.FromMinutes(1));

    /// <summary>
    /// 自定义端点（host + RPM）→ 闸门实例的进程级缓存。
    /// <b>必须缓存</b>：<see cref="RateGate"/> 的滑动窗口记录在实例字段 <c>_stamps</c> 里，
    /// 每次调用都 new 一枚闸门 → 队列恒为空 → <c>_stamps.Count &lt; _maxRequests</c> 恒真 → 一次都不等待，
    /// 用户填的「每分钟请求上限」会静默失效（P18：静默失败才是真凶）。
    /// 按 (host, rpm) 组合建键：同一端点改了 RPM 上限会拿到新闸门，不会沿用旧的窗口记录。
    /// </summary>
    private static readonly ConcurrentDictionary<string, RateGate> CustomGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 智谱（open.bigmodel.cn）策略档。
    /// <b>官方限流维度是「并发数」而非 RPM</b>：本应用的 AI 调用严格串行（同一时刻在途恒为 1），
    /// 已天然规避 1302 并发超限 → 不加闸门（加了只会人为降速，零收益）。
    /// <b>将来若引入并行调用，必须改为并发闸门（SemaphoreSlim），不是 RPM 闸门。</b>
    /// 退避单次封顶收到 30s：智谱的限流恢复较快，60s 的封顶对它偏保守。
    /// </summary>
    public static AiProviderProfile Zhipu { get; } = new()
    {
        Name = "Zhipu",
        Gate = null,
        Retry = new AiRetryPolicy { MaxDelay = TimeSpan.FromSeconds(30) },
        RetryAfterHeaders = RetryAfterParser.StandardHeaders,
        RetryAfterFromJson = true,
        Permanent = new AiPermanentRuleSet { BusinessCodes = BuildZhipuPermanentCodes() },
    };

    /// <summary>
    /// 通义千问（DashScope）策略档。
    /// 官方 1200 RPM / 1M TPM 是 qwen-vl-max 的额度，当前使用的 qwen-vl-plus 未查到公开数值；
    /// 但串行调用下实际只有 3~20 RPM，与 1200 差两个数量级 → 闸门永不触发，故不加。
    /// 阿里的 401 InvalidApiKey / 403 Model.AccessDenied / 403 AllocationQuota.FreeTierOnly /
    /// 400 Arrearage 全是 4xx，已由现有「4xx 非 429 → AiPermanentException」分支覆盖，无需入表；
    /// <b>Throttling.* 系列（限流）全部可重试，绝不入表。</b>
    /// </summary>
    public static AiProviderProfile Qwen { get; } = new()
    {
        Name = "Qwen",
        Gate = null,
        Retry = new AiRetryPolicy(),
        RetryAfterHeaders = RetryAfterParser.StandardHeaders,
        RetryAfterFromJson = true,
        Permanent = new AiPermanentRuleSet(),
    };

    /// <summary>
    /// NVIDIA（integrate.api.nvidia.com）策略档：唯一默认带 RPM 闸门的档位（30 RPM）。
    /// 403（模型未在该账号下注册）不进永久码表：已由 4xx 分支覆盖，并保留 NVIDIA 定向引导文案。
    /// </summary>
    public static AiProviderProfile Nvidia { get; } = new()
    {
        Name = "Nvidia",
        Gate = NvidiaGate,
        Retry = new AiRetryPolicy(),
        RetryAfterHeaders = RetryAfterParser.StandardHeaders,
        RetryAfterFromJson = true,
        Permanent = new AiPermanentRuleSet(),
    };

    /// <summary>
    /// 通用档（OpenAI 兼容端点 / 未知供应商 / 自建服务 / localhost）：不主动限速，
    /// 重试提示头覆盖 OpenAI 系与 Anthropic 系，永久规则为空表（保守：只靠状态码判定）。
    /// </summary>
    public static AiProviderProfile Generic { get; } = new()
    {
        Name = "Generic",
        Gate = null,
        Retry = new AiRetryPolicy(),
        RetryAfterHeaders = Union(RetryAfterParser.StandardHeaders, RetryAfterParser.AnthropicHeaders),
        RetryAfterFromJson = true,
        Permanent = new AiPermanentRuleSet(),
    };

    /// <summary>
    /// Anthropic（api.anthropic.com）策略档：通用档基础上
    /// ① 开启 <see cref="AiPermanentRuleSet.RetryAfterAbsentOn429IsPermanent"/>（兜底规则），
    /// ② 填 <see cref="AiPermanentRuleSet.DetailErrorCodes"/>（优先的结构化判据）。
    /// 该规则<b>只在 host 命中 api.anthropic.com 时开启</b>，全局开启会把智谱/阿里/NVIDIA/SiliconFlow
    /// 的瞬时限流全部误判成永久（它们同样不发 429 提示头），触发整批熔断（P17/P26 红线）。
    /// </summary>
    public static AiProviderProfile Anthropic { get; } = new()
    {
        Name = "Anthropic",
        Gate = null,
        Retry = new AiRetryPolicy(),
        RetryAfterHeaders = Union(RetryAfterParser.StandardHeaders, RetryAfterParser.AnthropicHeaders),
        RetryAfterFromJson = true,
        Permanent = new AiPermanentRuleSet
        {
            RetryAfterAbsentOn429IsPermanent = true, // 兜底：见 BuildAnthropicPermanentDetailCodes 注释
            DetailErrorCodes = BuildAnthropicPermanentDetailCodes(),
        },
    };

    /// <summary>
    /// Gemini（generativelanguage.googleapis.com）策略档：重试提示只在响应体 JSON 的
    /// <c>error.details[].retryDelay</c> 里给出，故显式开启 <see cref="AiProviderProfile.RetryAfterFromJson"/>。
    /// （该字段类型默认值本就是 true，此处显式写出是为了让「Gemini 依赖 JSON 提示」这一事实
    /// 不被将来对通用档的调整悄悄改掉。）
    /// </summary>
    public static AiProviderProfile Gemini { get; } = new()
    {
        Name = "Gemini",
        Gate = null,
        Retry = new AiRetryPolicy(),
        RetryAfterHeaders = Union(RetryAfterParser.StandardHeaders, RetryAfterParser.AnthropicHeaders),
        RetryAfterFromJson = true,
        Permanent = new AiPermanentRuleSet(),
    };

    /// <summary>按枚举取策略档；<c>Custom</c> 会按端点 host 嗅探（见 <see cref="ForCustom"/>）。</summary>
    /// <param name="provider">引擎枚举。</param>
    /// <param name="endpoint">自定义引擎的端点 URL（仅 <c>Custom</c> 使用）。</param>
    /// <param name="rpmLimit">自定义引擎的每分钟请求上限（仅 <c>Custom</c> 使用）；0 / null = 不限。</param>
    public static AiProviderProfile For(AiProvider provider, string endpoint = "", int? rpmLimit = null)
        => provider switch
        {
            AiProvider.Zhipu => Zhipu,
            AiProvider.Qwen => Qwen,
            AiProvider.Nvidia => Nvidia,
            AiProvider.Custom => ForCustom(endpoint, rpmLimit),
            _ => Generic,
        };

    /// <summary>
    /// 自定义端点：按 <see cref="Uri.Host"/> 关键字嗅探供应商，命中即用其策略档，未命中用通用档。
    /// </summary>
    /// <param name="endpoint">完整端点 URL（解析不出 host 时按通用档处理）。</param>
    /// <param name="rpmLimit">用户显式指定的每分钟请求上限；<c>&gt; 0</c> 时覆盖嗅探结果的闸门，
    /// <c>0</c> 或 null 时保持原档位（不替用户猜 RPM）。</param>
    public static AiProviderProfile ForCustom(string endpoint, int? rpmLimit = null)
    {
        var host = "";
        if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            host = uri.Host;

        AiProviderProfile profile;
        if (HostMatches(host, "open.bigmodel.cn")) profile = Zhipu;
        else if (HostMatches(host, "dashscope.aliyuncs.com")) profile = Qwen;
        else if (HostMatches(host, "api.anthropic.com")) profile = Anthropic;
        else if (HostMatches(host, "generativelanguage.googleapis.com")) profile = Gemini;
        // 以下三家均为 OpenAI 兼容且无结构化错误码，统一走通用档：
        // - api.openai.com：错误体有结构化 code，但限流类均可重试，靠状态码足够
        // - api.moonshot.cn（Kimi）：同 OpenAI 兼容
        // - api.siliconflow.cn：429 是纯文本、无结构化错误码，只能靠状态码判定，
        //   因此 BodySnippets 子串匹配对其不启用（易误伤）
        // - openrouter.ai：402 = credits 耗尽，属 4xx，已由 4xx 分支覆盖
        // 其余（含 localhost / 127.0.0.1 / 企业自建网关）同样按通用档处理
        else profile = Generic;

        if (!rpmLimit.HasValue || rpmLimit.Value <= 0) return profile;

        // init 属性不可变：需要新构造一份档位（不能用 with——with 对非 record 的 class 不适用）。
        // 闸门必须走进程级缓存（见 CustomGates 注释）：每次 new 都会让滑动窗口清零、限速静默失效。
        return new AiProviderProfile
        {
            Name = profile.Name + "(rpm<= " + rpmLimit.Value.ToString(CultureInfo.InvariantCulture) + ")",
            Gate = CustomGates.GetOrAdd(host + "|" + rpmLimit.Value.ToString(CultureInfo.InvariantCulture),
                _ => new RateGate(rpmLimit.Value, TimeSpan.FromMinutes(1))),
            Retry = profile.Retry,
            RetryAfterHeaders = profile.RetryAfterHeaders,
            RetryAfterFromJson = profile.RetryAfterFromJson,
            Permanent = profile.Permanent,
        };
    }

    /// <summary>
    /// host 与供应商关键字是否匹配：<b>精确相等或其子域</b>。
    /// 不能用子串匹配（<c>host.IndexOf(keyword) &gt;= 0</c>）——那样
    /// <c>notapi.anthropic.com.attacker.cn</c> 会命中 <c>api.anthropic.com</c>，
    /// 把 <see cref="AiPermanentRuleSet.RetryAfterAbsentOn429IsPermanent"/> 这个
    /// 「误判即熔断整批」（P26）的高危开关授予伪造 host。
    /// 子域放行是必要的：官方网关本身会用子域（如 <c>open.bigmodel.cn</c> 的同族域名）。
    /// </summary>
    private static bool HostMatches(string host, string keyword)
        => host.Length > 0 &&
           (host.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + keyword, StringComparison.OrdinalIgnoreCase));

    private static string[] Union(string[] a, string[] b)
    {
        var list = new List<string>(a.Length + b.Length);
        list.AddRange(a);
        list.AddRange(b);
        return list.ToArray();
    }

    /// <summary>
    /// 智谱 HTTP 429 中的「账户级永久错误」业务码：命中即判永久，重试无意义
    /// （否则单文件会空转到退避预算耗尽，再被上层重排队 10 次）。
    /// 依据：https://docs.bigmodel.cn/cn/api/api-code（官方错误码表，2026-09 取证）。
    ///
    /// <b>入表判据：该码对应的限制「不会在合理等待内自行恢复」</b>——判永久的代价是放弃整批
    ///（连续 3 个文件命中即中止，P26），所以只有充值 / 续费 / 配置级 / 周+ 级周期才可入表。
    ///
    /// <b>显式排除 1302（并发超限）与 1305（平台过载）：这两个是可重试的瞬时错误，绝不可入表。</b>
    ///
    /// <b>显式排除 1308 / 1316 / 1318 / 1320（逐码理由见下）：</b>
    /// - 1316 = 5 小时使用上限（主账号余额不足）；1318 = 5 小时 + 子账号月上限；
    ///   1320 = 5 小时 + 企业级月上限 —— 官方按「5 小时窗口」归类，窗口过后自动恢复。
    ///   判永久会把「等一会儿 / 重试几次就好」的场景直接放弃整批（误杀）。
    /// - 1308 = 已达到 ${number} ${unit} 使用上限，限额将在 ${next_flush_time} 重置 ——
    ///   <b>周期未写明、unit 是变量</b>，不能假定是长周期，一律按可重试处理。
    ///
    /// 注：官方的 <c>next_flush_time</c> 只出现在 <c>error.message</c> 文案里（占位符），
    /// 不是响应体的独立 JSON 字段（响应体只有 error.code + error.message）——
    /// <b>不要尝试解析它</b>，重试提示一律交给 Retry-After / 退避。
    /// </summary>
    private static Dictionary<string, string> BuildZhipuPermanentCodes() => new(StringComparer.Ordinal)
    {
        ["1113"] = "账户欠费",                                   // 需充值，不会自行恢复
        ["1309"] = "套餐到期",                                    // 需续费，不会自行恢复
        ["1310"] = "已达每周/每月使用上限",                        // 官方明确 周 / 月 级周期
        ["1311"] = "无该模型权限",                                 // 配置级，不会自行恢复
        ["1314"] = "企业套餐失效",                                 // 配置级，不会自行恢复
        ["1315"] = "API Key 类型不匹配",                           // 配置级，不会自行恢复
        ["1317"] = "已达 7 天使用上限",                            // 官方明确 7 天周期
        ["1319"] = "已达 7 天使用上限，且已达子账号月消费上限",      // 7 天 + 月，官方口径为 7 天
        ["1321"] = "已达 7 天使用上限，且已达企业级月消费上限",      // 7 天 + 月，官方口径为 7 天
        ["1000"] = "鉴权失败",
        ["1001"] = "鉴权失败",
        ["1002"] = "鉴权失败",
        ["1003"] = "鉴权失败",
        ["1004"] = "鉴权失败",
        ["1005"] = "鉴权失败",
        ["1220"] = "无权限",
        ["1222"] = "API 不存在",
    };

    /// <summary>
    /// Anthropic 的结构化永久错误码（读 <c>error.details.error_code</c>）。
    /// 依据 https://platform.claude.com/docs/en/api/rate-limits （2026-09 取证）：
    /// 官方明确用 <c>enforced_spend_limit_reached</c> "to tell this response apart from a rate limit"，
    /// 这是比「429 且无 retry-after」启发式可靠得多的判据，故<b>优先使用本表</b>，
    /// <see cref="AiPermanentRuleSet.RetryAfterAbsentOn429IsPermanent"/> 只作兜底。
    /// 两个已知陷阱：
    /// - <c>error.type</c> 是 <c>rate_limit_error</c>，与普通限流<b>同类型，不可用作判据</b>；
    /// - 用户自设 spend limit 走的是 HTTP 400 + <c>invalid_request_error</c>（不是 429），
    ///   已由 4xx 分支覆盖，不在本表内。
    /// </summary>
    private static Dictionary<string, string> BuildAnthropicPermanentDetailCodes() => new(StringComparer.Ordinal)
    {
        ["enforced_spend_limit_reached"] = "账户支出上限已耗尽（enforced spend limit），重试无意义",
    };
}
