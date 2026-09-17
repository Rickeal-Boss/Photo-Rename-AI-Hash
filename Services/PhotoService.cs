using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
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
    /// <remarks>
    /// P0-1 修复：EXIF 日期标签的原始串形如 "2001:01:28 13:59:33"（日期段用冒号分隔），
    /// 不是任何主流文化的合法日期格式，此前用 DateTime.TryParse 解析在 zh-CN/en-US 下
    /// 恒定失败（实测验证）→ 所有文件静默回退文件修改时间，"按拍摄时间"名存实亡。
    /// 现改用 MetadataExtractor 自带的 TryGetDateTime 扩展：内部按 EXIF 专用模式
    /// （yyyy:MM:dd HH:mm:ss 等）+ InvariantCulture 解析，跨文化稳定。
    /// 同时改为按目录类型（ExifSubIfd / ExifIfd0）精确取值：tag ID 仅在所属目录内有意义，
    /// 旧写法用同一 tag ID 扫全部目录存在跨目录误读隐患。
    /// </remarks>
    public DateTime? GetDateTaken(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            // 优先级不变：0x9003 DateTimeOriginal → 0x9004 DateTimeDigitized（均在 ExifSubIfd）→ 0x0132 DateTime（Ifd0）
            foreach (var dir in directories.OfType<ExifSubIfdDirectory>())
            {
                if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt)) return Normalize(dt);
                if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out dt)) return Normalize(dt);
            }

            foreach (var dir in directories.OfType<ExifIfd0Directory>())
            {
                if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt)) return Normalize(dt);
            }
        }
        catch
        {
            // 不支持的格式或损坏的文件：忽略
        }

        return null;
    }

    /// <summary>
    /// EXIF 时间无时区语义；个别带时区偏移的标签会被库解析为 Kind=Utc（内部 AdjustToUniversal），
    /// 转回本地墙钟时间，与 LastModified（本地时间）的回退口径保持一致（见 ScanAsync 注释）。
    /// </summary>
    private static DateTime Normalize(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
}
