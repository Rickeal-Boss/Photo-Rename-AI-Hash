using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetadataExtractor;
using PhotoRenameAIHash.Models;

namespace PhotoRenameAIHash.Services;

public sealed class PhotoService : IPhotoService
{
    private static readonly string[] ImageExt =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public async Task<IReadOnlyList<PhotoFile>> ScanAsync(string folder, CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(folder)) return Array.Empty<PhotoFile>();

        var result = new List<PhotoFile>();
        await Task.Run(() =>
        {
            foreach (var path in EnumerateImages(folder, ct))
            {
                var info = new FileInfo(path);
                result.Add(new PhotoFile
                {
                    Path = path,
                    Name = info.Name,
                    SizeBytes = info.Length,
                    LastModified = info.LastWriteTime, // 本地时间，与 EXIF GetDateTaken 解析的本地时间保持一致
                });
            }
        }, ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>递归枚举目录下所有图片文件；遇到无访问权限的子目录则跳过（不中断整体扫描），与续传日志的递归加载保持一致。</summary>
    private static IEnumerable<string> EnumerateImages(string root, CancellationToken ct)
    {
        IEnumerable<string> files;
        try { files = System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Array.IndexOf(ImageExt, ext) >= 0)
                yield return f;
        }

        IEnumerable<string> dirs;
        try { dirs = System.IO.Directory.EnumerateDirectories(root); }
        catch { yield break; }

        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in EnumerateImages(d, ct))
                yield return f;
        }
    }

    /// <summary>读取照片 EXIF 拍摄时间（DateTimeOriginal），失败或无 EXIF 时返回 null。</summary>
    public DateTime? GetDateTaken(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            // EXIF 标签 ID：0x9003=DateTimeOriginal, 0x9004=DateTimeDigitized, 0x0132=DateTime(file change)
            foreach (var tag in new[] { 0x9003, 0x9004, 0x0132 })
            {
                foreach (var dir in directories)
                {
                    var raw = dir.GetDescription(tag);
                    if (!string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out var parsed))
                    {
                        return parsed;
                    }
                }
            }
        }
        catch
        {
            // 不支持的格式或损坏的文件：忽略
        }

        return null;
    }
}
