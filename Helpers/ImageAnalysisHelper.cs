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

    public static async Task<string?> CallVisionApiAsync(
        string endpoint, string model, string apiKey, string prompt, string dataUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

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

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
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
