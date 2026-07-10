using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public interface IImageAnalysisService
{
    AiProvider Provider { get; }

    /// <summary>分析单张图片，返回结构化结果；失败（无密钥/网络/HTTP/解析）时抛异常，绝不静默返回 null。</summary>
    Task<ImageAnalysisResult?> AnalyzeAsync(string imagePath, string language, CancellationToken ct = default);
}
