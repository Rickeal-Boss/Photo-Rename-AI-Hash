using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    public static async Task<byte[]> DecodeGrayAsync(Stream source, int width, int height, CancellationToken ct = default)
    {
        using var ras = new InMemoryRandomAccessStream();

        long remaining = source.Length - source.Position;
        if (remaining > MaxInputBytes)
            throw new InvalidOperationException("图片过大，已跳过解码（超过解码输入上限）。");
        var buffer = new byte[remaining];
        int read = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(buffer.AsSpan(0, read).ToArray());
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

        long remaining = source.Length - source.Position;
        if (remaining > MaxInputBytes)
            throw new InvalidOperationException("图片过大，已跳过解码（超过解码输入上限）。");
        var buffer = new byte[remaining];
        int read = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(buffer.AsSpan(0, read).ToArray());
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

        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream).AsTask(ct).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, nw, nh, 96, 96, pixels);
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
}
