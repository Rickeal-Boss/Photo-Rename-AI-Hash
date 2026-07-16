using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public interface IPhotoService
{
    Task<IReadOnlyList<PhotoFile>> ScanAsync(string folder, CancellationToken ct = default);

    Task<int> RenameAsync(IReadOnlyList<RenameItem> items, CancellationToken ct = default);

    /// <summary>读取 EXIF 拍摄时间；不可用时返回 null。</summary>
    DateTime? GetDateTaken(string filePath);
}
