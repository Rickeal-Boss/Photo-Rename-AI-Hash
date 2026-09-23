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

    /// <summary>
    /// 当前是否有批次处于活跃期（第十四轮 R3-2）：供 UI 区分「批次已结束但 VM 尚在收尾」
    /// 与「批次仍在准备阶段」——两者 IsBusy 都为 true，但只有后者值得提示「暂不支持暂停」。
    /// 注意必须落在<b>接口</b>上：VM 经 AppServices.OrganizeService 以接口类型访问，
    /// 只加在实现类上是 CS1061（第十四轮 CI 实证）。
    /// </summary>
    bool IsBatchActive { get; }
}
