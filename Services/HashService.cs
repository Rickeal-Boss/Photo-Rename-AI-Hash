using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PhotoRenameAIHash.Helpers;

namespace PhotoRenameAIHash.Services;

public sealed class HashService : IHashService
{
    public async Task<ulong> ComputeAHashAsync(Stream image, int size = 8, CancellationToken ct = default)
    {
        var gray = await ImageDecoder.DecodeGrayAsync(image, size, size, ct).ConfigureAwait(false);
        return AHashFromGray(gray, size);
    }

    public async Task<ulong> ComputeDHashAsync(Stream image, int size = 8, CancellationToken ct = default)
    {
        // dHash uses width = size + 1 so we can compare adjacent columns.
        var gray = await ImageDecoder.DecodeGrayAsync(image, size + 1, size, ct).ConfigureAwait(false);
        return DHashFromGray(gray, size + 1, size);
    }

    public int HammingDistance(ulong a, ulong b)
    {
        var x = a ^ b;
        int count = 0;
        while (x != 0)
        {
            x &= x - 1;
            count++;
        }

        return count;
    }

    public double Similarity(ulong a, ulong b)
    {
        const int bits = 8 * 8; // 64-bit hashes
        return 1.0 - (double)HammingDistance(a, b) / bits;
    }

    // ---- MD5（用于冲突检测与去重库） ----

    public async Task<string> ComputeMd5Async(Stream data, CancellationToken ct = default)
    {
        using var md5 = MD5.Create();
        var hash = await Task.Run(() => md5.ComputeHash(data), ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ---- 基于文件路径的便捷重载 ----

    public Task<ulong> ComputeAHashAsync(string filePath, int size = 8, CancellationToken ct = default)
    {
        using var s = File.OpenRead(filePath);
        return ComputeAHashAsync(s, size, ct);
    }

    public Task<ulong> ComputeDHashAsync(string filePath, int size = 8, CancellationToken ct = default)
    {
        using var s = File.OpenRead(filePath);
        return ComputeDHashAsync(s, size, ct);
    }

    public async Task<string> ComputeMd5Async(string filePath, CancellationToken ct = default)
    {
        using var s = File.OpenRead(filePath);
        return await ComputeMd5Async(s, ct).ConfigureAwait(false);
    }

    public async Task<ulong?> TryComputeAHashAsync(string filePath, int size = 8, CancellationToken ct = default)
    {
        try { return await ComputeAHashAsync(filePath, size, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    public async Task<ulong?> TryComputeDHashAsync(string filePath, int size = 8, CancellationToken ct = default)
    {
        try { return await ComputeDHashAsync(filePath, size, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    public async Task<string?> TryComputeMd5Async(string filePath, CancellationToken ct = default)
    {
        try { return await ComputeMd5Async(filePath, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static ulong AHashFromGray(byte[] gray, int size)
    {
        int total = 0;
        foreach (var v in gray) total += v;
        var mean = (byte)(total / gray.Length);

        ulong hash = 0;
        for (int i = 0; i < gray.Length; i++)
        {
            if (gray[i] >= mean) hash |= 1UL << i;
        }

        return hash;
    }

    private static ulong DHashFromGray(byte[] gray, int w, int h)
    {
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                int left = gray[y * w + x];
                int right = gray[y * w + x + 1];
                if (left < right) hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }
}
