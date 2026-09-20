using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoRenameAIHash.Helpers;

/// <summary>
/// Decodes an image stream into a grayscale pixel buffer at the requested size,
/// using only WinRT imaging APIs (no native dependencies).
/// </summary>
public static class ImageDecoder
{
    /// <summary>解码输入硬上限：超过此尺寸的文件直接抛异常进入「超限跳过」分支，避免单次解码把整文件读入内存造成 OOM。
    /// 对应安全设计 §4.3.5「单图解码缓冲」控制；实际解码像素缓冲由缩放目标尺寸决定本就很小，此处仅约束输入读取。</summary>
    private const long MaxInputBytes = 256L * 1024 * 1024; // 256 MB

    /// <summary>EXIF Orientation 标签（0x0112，位于 IFD0）：5/6/7/8 表示 90°/270° 旋转，
    /// 其中 6 是 iPhone / Android 竖拍照片的默认标记（本项目目标人群里占比最高的一类）。
    /// 这里直接写 tag 值而非引用库常量：该常量在 MetadataExtractor 各版本里归属类
    /// （ExifIfd0Directory / ExifDirectoryBase）不完全一致，写死可避免升级时的歧义。</summary>
    private const int ExifTagOrientation = 0x0112;

    /// <summary>把输入流循环读满到内存（ReadAsync 允许短读，必须循环），超过 maxBytes 抛异常。</summary>
    private static async Task<byte[]> ReadCappedAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await source.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > maxBytes)
                throw new InvalidOperationException("图片过大，已跳过解码（超过解码输入上限）。");
            await ms.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
        }
        return ms.ToArray();
    }

    public static async Task<byte[]> DecodeGrayAsync(Stream source, int width, int height, CancellationToken ct = default)
    {
        using var ras = new InMemoryRandomAccessStream();

        var bytes = await ReadCappedAsync(source, MaxInputBytes, ct).ConfigureAwait(false);
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
        }

        ras.Seek(0);
        ct.ThrowIfCancellationRequested();

        var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)height,
            InterpolationMode = BitmapInterpolationMode.Linear,
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Gray8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

        return pixelData.DetachPixelData();
    }

    /// <summary>
    /// Decodes an image, scales it down so its longest edge is at most <paramref name="maxDim"/>,
    /// and re-encodes as JPEG. Returns the base64 of the JPEG bytes (no data-URL prefix).
    /// Used to shrink photos before sending them to a vision model.
    /// </summary>
    public static async Task<string> EncodeResizedJpegAsync(Stream source, int maxDim, CancellationToken ct = default)
    {
        using var ras = new InMemoryRandomAccessStream();

        var bytes = await ReadCappedAsync(source, MaxInputBytes, ct).ConfigureAwait(false);
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
        }

        ras.Seek(0);
        ct.ThrowIfCancellationRequested();

        var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
        uint w = decoder.PixelWidth;
        uint h = decoder.PixelHeight;
        uint nw = w, nh = h;
        if (w > maxDim || h > maxDim)
        {
            if (w >= h) { nw = (uint)maxDim; nh = (uint)((double)h * maxDim / w); }
            else { nh = (uint)maxDim; nw = (uint)((double)w * maxDim / h); }
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = nw,
            ScaledHeight = nh,
            InterpolationMode = BitmapInterpolationMode.Linear,
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);

        var pixels = pixelData.DetachPixelData();

        // 上面算出的 nw/nh 处于「源坐标系」（Microsoft Learn：BitmapTransform.ScaledWidth/ScaledHeight
        // "is defined in the coordinate space of the source image, before rotation and flip are applied"），
        // 而 RespectExifOrientation 让返回的像素缓冲处于「已旋转坐标系」：Orientation 5/6/7/8 时宽高互换。
        // 若不互换就交给 SetPixelData，因 w*h*4 字节数恰好相等，编码不会报错、只静默产出斜切花屏图，
        // AI 看到废图 → 给出错误描述 → 生成错误的文件名。
        //
        // P1：判据改用 WinRT 自己的真值源 OrientedPixelWidth / OrientedPixelHeight ——
        // 它与上面 GetPixelDataAsync(..., RespectExifOrientation) 用的是同一份 EXIF 解析结果、与输出 100% 同源。
        // 旧判据 IsExifOrientationSwapsAxes 依赖 MetadataExtractor 解析成功，
        // 元数据损坏 / 无 EXIF / 某些 HEIC 会静默回退「不旋转」（该文件注释自陈），
        // 而下面的像素总数自检拦不住这种判错：宽高互换不改变 w*h 的乘积，自检 100% 通过。
        bool swapAxes;
        uint ow = decoder.OrientedPixelWidth;
        uint oh = decoder.OrientedPixelHeight;
        if (ow > 0 && oh > 0)
        {
            // 定向后宽高任一变化 ⟺ 发生了 90°/270° 旋转（Orientation 5/6/7/8）→ 宽高互换；
            // Orientation 2 / 3 / 4（镜像、180°）不改变宽高 → 不互换。
            // 正方形图 w == h 时两个分支等价，互换与否无差别。
            swapAxes = ow != w || oh != h;
        }
        else
        {
            // 兜底：正常解码器恒 > 0，这里只为「定向宽高不可用」的极端情形留退路，
            // 沿用旧的 EXIF 解析判据（失败模式与改造前一致，且仍受下面的像素总数自检保护）。
            swapAxes = IsExifOrientationSwapsAxes(bytes);
        }
        uint outW = swapAxes ? nh : nw;
        uint outH = swapAxes ? nw : nh;

        // 自检：缩放与旋转都不改变像素总数，尺寸不符说明坐标系判断有误（如 nw/nh 取整差异）。
        // 此时宁可抛错（上层按「超限 / 解码失败」跳过该文件）也不要产出损坏图像。
        // 注意：本自检拦不住「宽高互换判错」（乘积不变）——那一条已由上面的 OrientedPixelWidth 判据解决；
        // 但它仍能拦住取整差异等真实尺寸不符，不要删除。
        if (pixels.Length != (long)outW * outH * 4)
            throw new InvalidOperationException($"解码后的像素缓冲尺寸与预期不符（期望 {outW}x{outH}，实际 {pixels.Length / 4} 像素），已跳过该文件以避免产出花屏图。");

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream).AsTask(ct).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, outW, outH, 96, 96, pixels);
        encoder.IsThumbnailGenerated = false;
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

        var outBytes = new byte[(int)outStream.Size];
        using (var reader = new DataReader(outStream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)outStream.Size).AsTask(ct).ConfigureAwait(false);
            reader.ReadBytes(outBytes);
        }

        return Convert.ToBase64String(outBytes);
    }

    /// <summary>
    /// 读取 EXIF Orientation，判断像素缓冲相对源坐标系是否发生了宽高互换（值 5/6/7/8 = 90°/270° 旋转）。
    /// </summary>
    /// <remarks>
    /// <b>本方法现已降级为兜底</b>：主判据改用 <c>BitmapDecoder.OrientedPixelWidth</c> /
    /// <c>OrientedPixelHeight</c>（见 <c>EncodeResizedJpegAsync</c>）——它用的是 WinRT 自己解析的方向源，
    /// 与 <c>RespectExifOrientation</c> 的输出 100% 一致，且不依赖外部库解析成功。
    /// 本方法只在「定向宽高不可用（返回 0）」这一理论上才会被走到。
    /// 保留它的原因：MetadataExtractor 是本项目既有依赖、零额外成本，且它不需要文件路径——
    /// 直接复用 <c>EncodeResizedJpegAsync</c> 已读入内存的字节，调用方（ImageAnalysisHelper）无需任何改动。
    /// <b>它的已知弱点（所以才有主判据）：</b>格式不支持 / 元数据损坏 / 无 EXIF 时一律按「不旋转」处理，
    /// 而 WinRT 侧仍可能已按 EXIF 旋转 → 宽高互换判错 → 静默花屏；
    /// 像素总数自检拦不住这种情况，因为宽高互换不改变 <c>w*h</c> 的乘积。
    /// </remarks>
    private static bool IsExifOrientationSwapsAxes(byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            foreach (var dir in ImageMetadataReader.ReadMetadata(ms).OfType<ExifIfd0Directory>())
            {
                if (dir.ContainsTag(ExifTagOrientation) && TryGetInt32(dir.GetObject(ExifTagOrientation), out var orientation))
                    return orientation >= 5 && orientation <= 8;
            }
        }
        catch
        {
            // 格式不支持 / 元数据损坏 / 无 EXIF：按「不旋转」处理，回退到旧行为
        }

        return false;
    }

    /// <summary>把标签值安全转成 int：EXIF Orientation 的类型是 SHORT，库里可能存成 ushort / short / int / 字符串，
    /// 逐个兼容以免某个版本的 TryGetInt32 不做整型转换时静默取不到方向。</summary>
    private static bool TryGetInt32(object? value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case byte b: result = b; return true;
            case sbyte sb: result = sb; return true;
            case uint ui when ui <= int.MaxValue: result = (int)ui; return true;
            case long l when l >= int.MinValue && l <= int.MaxValue: result = (int)l; return true;
            case string str: return int.TryParse(str, out result);
            default: result = 0; return false;
        }
    }
}
