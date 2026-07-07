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
}
