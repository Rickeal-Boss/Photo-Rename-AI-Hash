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

    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        IEnumerable<PhotoFile> files,
        int aHashThreshold,
        int dHashThreshold,
        CancellationToken ct = default)
    {
        var list = files.ToList();
        var records = new List<(PhotoFile file, ulong a, ulong d)>();

        foreach (var pf in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = File.OpenRead(pf.Path);
                var a = await AppServices.HashService.ComputeAHashAsync(stream, 8, ct).ConfigureAwait(false);
                stream.Position = 0;
                var d = await AppServices.HashService.ComputeDHashAsync(stream, 8, ct).ConfigureAwait(false);
                records.Add((pf, a, d));
            }
            catch
            {
                // Skip unreadable or unsupported images.
            }
        }

        var groups = new List<DuplicateGroup>();
        var used = new bool[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            if (used[i]) continue;
            var grp = new DuplicateGroup { Id = groups.Count + 1 };
            grp.Members.Add(records[i].file);
            used[i] = true;

            for (int j = i + 1; j < records.Count; j++)
            {
                if (used[j]) continue;
                int da = AppServices.HashService.HammingDistance(records[i].a, records[j].a);
                int dd = AppServices.HashService.HammingDistance(records[i].d, records[j].d);
                if (da <= aHashThreshold || dd <= dHashThreshold)
                {
                    grp.Members.Add(records[j].file);
                    used[j] = true;
                }
            }

            if (grp.Members.Count > 1) groups.Add(grp);
        }

        return groups;
    }

    /// <summary>
    /// 精确哈希去重：以文件内容 MD5 为键聚合，找出"内容逐字节完全相同"的图片。
    /// 仅用于展示详细路径，不参与任何文件处理（删除/移动）。
    /// </summary>
    public async Task<IReadOnlyList<DuplicateGroup>> FindExactDuplicatesAsync(
        IEnumerable<PhotoFile> files,
        CancellationToken ct = default)
    {
        var list = files.ToList();
        var byHash = new Dictionary<string, DuplicateGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var pf in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var md5 = await AppServices.HashService.ComputeMd5Async(pf.Path, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(md5)) continue;

                if (!byHash.TryGetValue(md5, out var grp))
                {
                    grp = new DuplicateGroup { Hash = md5, KeepIndex = 0 };
                    byHash[md5] = grp;
                }

                grp.Members.Add(pf);
            }
            catch
            {
                // Skip unreadable or locked files.
            }
        }

        var groups = new List<DuplicateGroup>();
        int id = 1;
        foreach (var g in byHash.Values)
        {
            if (g.Members.Count > 1)
            {
                g.Id = id++;
                groups.Add(g);
            }
        }

        return groups;
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
