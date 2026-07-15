using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public interface IPhotoService
{
    Task<IReadOnlyList<PhotoFile>> ScanAsync(string folder, CancellationToken ct = default);

    Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IEnumerable<PhotoFile> files,
        int aHashThreshold,
        int dHashThreshold,
        CancellationToken ct = default);

    Task<int> RenameAsync(IReadOnlyList<RenameItem> items, CancellationToken ct = default);

    /// <summary>
    /// 精确哈希去重：按文件内容 MD5 分组，仅聚集"内容完全相同"的图片。
    /// 返回的成员数 > 1 的组即真正重复组（单向展示，不删除）。
    /// </summary>
    Task<IReadOnlyList<DuplicateGroup>> FindExactDuplicatesAsync(
        IEnumerable<PhotoFile> files,
        CancellationToken ct = default);

    /// <summary>读取 EXIF 拍摄时间；不可用时返回 null。</summary>
    DateTime? GetDateTaken(string filePath);
}
