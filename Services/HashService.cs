using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoRenameAIHash.Services;

public sealed class HashService : IHashService
{
    // ---- MD5：文件内容指纹，用于冲突检测与重命名日志 ----

    public async Task<string> ComputeMd5Async(Stream data, CancellationToken ct = default)
    {
        using var md5 = MD5.Create();
        var hash = await Task.Run(() => md5.ComputeHash(data), ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<string> ComputeMd5Async(string filePath, CancellationToken ct = default)
    {
        using var s = File.OpenRead(filePath);
        return await ComputeMd5Async(s, ct).ConfigureAwait(false);
    }

    public async Task<string?> TryComputeMd5Async(string filePath, CancellationToken ct = default)
    {
        try { return await ComputeMd5Async(filePath, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
