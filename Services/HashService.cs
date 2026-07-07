using System;
using System.IO;
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
