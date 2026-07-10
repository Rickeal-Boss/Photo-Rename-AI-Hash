using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public interface IImageAnalysisService
{
    AiProvider Provider { get; }

    /// <summary>分析单张图片，返回结构化结果；失败或无密钥时返回 null。</summary>
    Task<ImageAnalysisResult?> AnalyzeAsync(string imagePath, string language, CancellationToken ct = default);
}
