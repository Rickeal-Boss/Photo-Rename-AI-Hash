using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public interface IOrganizeService
{
    /// <summary>按命名模板 + 操作模式整理源文件夹。</summary>
    Task<OrganizeReport> RunAsync(OrganizeRequest request, IProgress<OrganizeProgress> progress, CancellationToken ct = default);

    /// <summary>按拍摄日期归档到 输出文件夹\yyyy\yyyy-MM-dd\。</summary>
    Task<OrganizeReport> ArchiveByDateAsync(OrganizeRequest request, IProgress<OrganizeProgress> progress, CancellationToken ct = default);

    /// <summary>软暂停：挂起执行循环（不取消，可恢复）。</summary>
    void Pause();

    /// <summary>恢复：放行被暂停的执行循环。</summary>
    void Resume();

    /// <summary>当前是否处于暂停状态。</summary>
    bool IsPaused { get; }
}
