using System.Threading;
using System.Threading.Tasks;

namespace PhotoRenameAIHash.Services;

public interface IHashService
{
    /// <summary>MD5 of file content; throws if the file is unreadable.</summary>
    Task<string> ComputeMd5Async(string filePath, CancellationToken ct = default);

    /// <summary>Best-effort MD5; returns null if the file is unreadable/unsupported.</summary>
    Task<string?> TryComputeMd5Async(string filePath, CancellationToken ct = default);
}
