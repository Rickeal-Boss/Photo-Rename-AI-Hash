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
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".jfif" };

    /// <summary>
    /// 文件枚举选项。<c>AttributesToSkip = 0</c> 刻意沿用旧的 Compatible 语义：隐藏 / 系统文件照旧会被扫描，
    /// 否则会漏掉用户放在隐藏目录里的照片（属产品口径，未拍板前不变更）。
    /// <c>IgnoreInaccessible = true</c> 让无权限文件在枚举期被跳过而不是抛异常。
    /// </summary>
    /// <remarks>
    /// <b>为什么目录侧跳过重解析点、文件侧不跳（别把两者「统一化」）：</b>
    /// <list type="bullet">
    /// <item>目录侧必须跳：junction / 符号链接目录是<b>递归路径</b>，指向祖先时会无限递归直至 OOM 挂死。</item>
    /// <item>文件侧必须不跳：符号链接的<b>照片文件</b>只是「指向别处的文件」，被跳过就是<b>静默漏扫</b>——丢数据，且无任何补救。</item>
    /// <item>代价对比：不跳的代价是用户自建 junction 被展开、同一批照片可能重复（<b>重复，不丢</b>，
    /// 且 <c>ResolveTargetAsync</c> 的 <c>targetMd5 == md5</c> 内容比对还能兜底）；跳过则是漏扫，无补救。
    /// 丢数据严重于重复，故选 0。</item>
    /// <item>不要再加自环 / 循环检测（如 <c>ResolveLinkTarget</c>）：目录侧已阻断递归入口，文件侧链接不构成递归路径，
    /// 加了只是大目录上的纯开销。</item>
    /// </list>
    /// </remarks>
    private static readonly EnumerationOptions FileOptions = new()
    {
        RecurseSubdirectories = false,
        MatchType = MatchType.Win32, // 与旧的 Compatible 语义保持一致（DOS 通配匹配）
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    /// <summary>
    /// 目录枚举选项：只额外跳过 <see cref="FileAttributes.ReparsePoint"/>（junction / 符号链接目录）。
    /// 旧的 Compatible 语义 AttributesToSkip=0 会把 junction 当普通目录递归进去，
    /// 而 Windows 用户目录里遍地是指向祖先的 junction（…\AppData\Local\Application Data → …\AppData\Local），
    /// 会造成无限递归、内存耗尽、应用挂死；非循环的 junction 则会让同一批照片被扫描并处理两遍。
    /// 注意：此处同样<b>不要</b>加 Hidden / System，理由与 <see cref="FileOptions"/> 一致。
    /// </summary>
    private static readonly EnumerationOptions DirOptions = new()
    {
        RecurseSubdirectories = false,
        MatchType = MatchType.Win32,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public async Task<IReadOnlyList<PhotoFile>> ScanAsync(string folder, CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(folder)) return Array.Empty<PhotoFile>();

        var result = new List<PhotoFile>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var path in EnumerateImages(folder, ct))
                {
                    try
                    {
                        // 文件可能在枚举之后被删除 / 独占锁定，Length / LastWriteTime 是一次 I/O 访问，
                        // 单点失败必须只跳过该文件，不能让整批扫描归零。
                        var info = new FileInfo(path);
                        result.Add(new PhotoFile
                        {
                            Path = path,
                            Name = info.Name,
                            SizeBytes = info.Length,
                            LastModified = info.LastWriteTime, // 本地时间，与 EXIF GetDateTaken 解析的本地时间保持一致
                        });
                    }
                    catch
                    {
                        // 单文件元数据读取失败：跳过，不中断整批扫描
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户取消：保持取消语义，不能包装成「扫描失败」
        }
        catch (Exception ex)
        {
            // 兜底：单个无权限目录已在 EnumerateImages 内被跳过，走到这里是枚举之外的意外。
            // 留一份可诊断的现场，再抛带目录信息的异常交给 UI 提示，而不是静默返回空列表。
            TryAppendScanLog($"扫描目录失败 folder={folder}{Environment.NewLine}{ex}");
            throw new InvalidOperationException($"扫描目录失败（{folder}）：{ex.Message}", ex);
        }

        return result;
    }

    /// <summary>递归枚举目录下所有图片文件；无权限目录被跳过（不中断整体扫描），与续传日志的递归加载保持一致。</summary>
    private static IEnumerable<string> EnumerateImages(string root, CancellationToken ct)
        => EnumerateImages(root, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 递归枚举目录下所有图片文件。
    /// <para>缺陷 A（junction 无限递归）：目录枚举走 <see cref="DirOptions"/>，重解析点目录不进入；
    /// 并额外用 visited 集合按规范化路径去重，防御硬链接、大小写与尾部分隔符变体造成的重复扫描。</para>
    /// <para>缺陷 B（无权限目录让整批失败）：<c>Directory.Enumerate*</c> 是惰性枚举，
    /// 异常在 foreach 的 MoveNext() 时抛出、不在调用时；旧写法把 try/catch 只套在迭代器创建上等于没保护。
    /// 这里改为先在 try 内物化（强制枚举跑完，异常才能被捕获），再在 try 外产出，
    /// 保证无权限目录只是被跳过。</para>
    /// </summary>
    private static IEnumerable<string> EnumerateImages(string root, CancellationToken ct, HashSet<string> visited)
    {
        if (!visited.Add(NormalizeDirectory(root))) yield break; // 已扫描过（含大小写 / 尾部分隔符变体）

        var files = SafeMaterialize(() => System.IO.Directory.EnumerateFiles(root, "*", FileOptions));
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Array.IndexOf(ImageExt, ext) >= 0)
                yield return f;
        }

        var dirs = SafeMaterialize(() => System.IO.Directory.EnumerateDirectories(root, "*", DirOptions));
        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var f in EnumerateImages(d, ct, visited))
                yield return f;
        }
    }

    /// <summary>
    /// 在 try 内把惰性枚举物化成 List：只有真正跑完枚举，MoveNext() 抛出的异常才会落到 catch。
    /// 捕获后返回空列表，语义为「跳过该目录」，不中断整体扫描。
    /// </summary>
    private static List<string> SafeMaterialize(Func<IEnumerable<string>> enumerate)
    {
        try { return new List<string>(enumerate()); }
        catch { return new List<string>(); }
    }

    /// <summary>目录路径规范化（展开相对路径与 ..、去掉尾部目录分隔符），用作去重集合的键。</summary>
    private static string NormalizeDirectory(string path)
    {
        try { path = Path.GetFullPath(path); }
        catch { /* 路径非法：保留原值，交由后续枚举自行失败 */ }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 扫描异常诊断日志（最佳努力；写日志失败绝不能再抛，否则会掩盖真实故障）。
    /// 落在 <c>%USERPROFILE%\.PhotoRenameAIHash\</c>，与 settings.json / crash.log 同目录——
    /// <b>刻意不用 <c>%LOCALAPPDATA%</c></b>：MSIX 会把它纳入包数据、卸载 / 重置应用时一并删除
    /// （判据见 <c>SettingsService.FilePath</c> 注释），而扫描现场正是排障时要留存的东西。
    /// </summary>
    private static void TryAppendScanLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".PhotoRenameAIHash");
            // 全限定 System.IO.Directory：本文件同时 using 了 MetadataExtractor，
            // 后者也有 Directory 类型（EXIF 目录项），裸写会触发 CS0104 歧义
            System.IO.Directory.CreateDirectory(dir);
            // 第十四轮 R5-1：与 App.WriteCrashLog 同批原子修复——诊断日志时间戳补 InvariantCulture
            //（非公历区域年份错位），ab51705 的「17 处全部补齐」漏掉的另一处。
            File.AppendAllText(Path.Combine(dir, "scan.log"),
                $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志不可写（如磁盘满 / 权限不足）：静默放弃，主流程不受影响
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
    private static DateTime Normalize(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc) dt = dt.ToLocalTime();
        return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified); // 统一 Unspecified 语义，避免下游隐式时区换算
    }
}
