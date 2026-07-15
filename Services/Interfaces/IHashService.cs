using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoRenameAIHash.Services;

public interface IHashService
{
    Task<ulong> ComputeAHashAsync(Stream image, int size = 8, CancellationToken ct = default);

    Task<ulong> ComputeDHashAsync(Stream image, int size = 8, CancellationToken ct = default);

    int HammingDistance(ulong a, ulong b);

    /// <summary>Similarity in [0,1]; 1 means identical.</summary>
    double Similarity(ulong a, ulong b);

    /// <summary>Best-effort perceptual hash; returns null if the file is unreadable/unsupported.</summary>
    Task<ulong?> TryComputeAHashAsync(string filePath, int size = 8, CancellationToken ct = default);

    /// <summary>Best-effort perceptual hash; returns null if the file is unreadable/unsupported.</summary>
    Task<ulong?> TryComputeDHashAsync(string filePath, int size = 8, CancellationToken ct = default);

    /// <summary>Best-effort MD5; returns null if the file is unreadable/unsupported.</summary>
    Task<string?> TryComputeMd5Async(string filePath, CancellationToken ct = default);

    /// <summary>MD5 of file content; throws if the file is unreadable.</summary>
    Task<string> ComputeMd5Async(string filePath, CancellationToken ct = default);
}
