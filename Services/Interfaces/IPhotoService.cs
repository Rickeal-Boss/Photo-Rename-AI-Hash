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
}
