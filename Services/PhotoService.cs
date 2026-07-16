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
            foreach (var path in System.IO.Directory.EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (Array.IndexOf(ImageExt, ext) < 0) continue;

                var info = new FileInfo(path);
                result.Add(new PhotoFile
                {
                    Path = path,
                    Name = info.Name,
                    SizeBytes = info.Length,
                    LastModified = info.LastWriteTimeUtc,
                });
            }
        }, ct).ConfigureAwait(false);

        return result;
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

    public async Task<int> RenameAsync(IReadOnlyList<RenameItem> items, CancellationToken ct = default)
    {
        int done = 0;
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(item.SourcePath) && !File.Exists(item.DestinationPath))
                {
                    File.Move(item.SourcePath, item.DestinationPath);
                    done++;
                }
            }
        }, ct).ConfigureAwait(false);

        return done;
    }
}
