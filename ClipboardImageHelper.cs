using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentClip;

public sealed class CapturedImageResult
{
    public required byte[] PngBytes { get; init; }
    public required byte[] ThumbnailPngBytes { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string SourceFilePath { get; init; } = string.Empty;
}

internal static unsafe class ClipboardImageHelper
{
    private static readonly uint PngClipFormat = NativeMethods.RegisterClipboardFormatW("PNG");
    private static readonly uint MimePngClipFormat = NativeMethods.RegisterClipboardFormatW("image/png");

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".tiff"
    };

    public static bool IsImageFile(string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    /// <summary>
    /// Captures image from an already-opened Win32 clipboard, prioritizing PNG stream (preserves alpha) -> CF_DIBV5 -> CF_DIB.
    /// Returns raw PNG bytes or raw BGRA pixels to be encoded outside the clipboard lock.
    /// </summary>
    public static (byte[]? RawPng, byte[]? BgraPixels, int Width, int Height) ReadRawImageFromOpenClipboard()
    {
        // 1. Prefer registered "PNG" or "image/png" format to preserve 100% lossless alpha transparency
        uint pngFmt = NativeMethods.IsClipboardFormatAvailable(PngClipFormat) ? PngClipFormat
            : (NativeMethods.IsClipboardFormatAvailable(MimePngClipFormat) ? MimePngClipFormat : 0);

        if (pngFmt != 0)
        {
            byte[]? bytes = ReadClipboardGlobalBytes(pngFmt);
            if (bytes != null && bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                return (bytes, null, 0, 0);
            }
        }

        // 2. Fallback to CF_DIBV5 or CF_DIB
        uint dibFmt = NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIBV5) ? NativeMethods.CF_DIBV5
            : (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB) ? NativeMethods.CF_DIB : 0);

        if (dibFmt != 0)
        {
            byte[]? dib = ReadClipboardGlobalBytes(dibFmt);
            if (dib != null && TryConvertDibToBgra32(dib, out byte[]? bgra, out int w, out int h) && bgra != null)
            {
                return (null, bgra, w, h);
            }
        }

        return (null, null, 0, 0);
    }

    public static List<string> ReadFileDropListFromOpenClipboard()
    {
        var result = new List<string>();
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
            return result;

        nint hDrop = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
        if (hDrop == 0) return result;

        uint count = NativeMethods.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0) return result;

        char* buf = stackalloc char[1024];
        for (uint i = 0; i < count; i++)
        {
            uint len = NativeMethods.DragQueryFileW(hDrop, i, buf, 1024);
            if (len > 0)
            {
                result.Add(new string(buf, 0, (int)len));
            }
        }
        return result;
    }

    public static byte[]? ReadClipboardGlobalBytes(uint format)
    {
        nint hMem = NativeMethods.GetClipboardData(format);
        if (hMem == 0) return null;

        nuint size = NativeMethods.GlobalSize(hMem);
        if (size == 0 || size > 150 * 1024 * 1024) return null; // cap at 150MB safety

        nint ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == 0) return null;

        try
        {
            byte[] data = new byte[(int)size];
            Marshal.Copy(ptr, data, 0, (int)size);
            return data;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }
    }

    /// <summary>
    /// Parses Windows BITMAPINFOHEADER / BITMAPV5HEADER (handling 24bpp & 32bpp, BI_RGB & BI_BITFIELDS, bottom-up & top-down) into top-down BGRA8.
    /// </summary>
    private static bool TryConvertDibToBgra32(byte[] dib, out byte[]? bgraOut, out int width, out int height)
    {
        bgraOut = null;
        width = 0;
        height = 0;

        if (dib.Length < 40) return false;

        uint biSize = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(0, 4));
        int biWidth = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4, 4));
        int biHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4));
        ushort biBitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14, 2));
        uint biCompression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4));

        if (biWidth <= 0 || biWidth > 16384 || biHeight == 0) return false;
        bool bottomUp = biHeight > 0;
        int absHeight = Math.Abs(biHeight);
        if (absHeight > 16384) return false;

        if (biBitCount != 32 && biBitCount != 24) return false;
        if (biCompression != 0 && biCompression != 3) return false; // BI_RGB=0, BI_BITFIELDS=3

        int pixelOffset = (int)biSize;
        if (biSize == 40 && biCompression == 3)
        {
            // Standard BITMAPINFOHEADER with BI_BITFIELDS appends 3 DWORD color masks (12 bytes)
            pixelOffset += 12;
        }

        int srcStride = ((biWidth * biBitCount + 31) / 32) * 4;
        if (pixelOffset < 0 || pixelOffset + (long)srcStride * absHeight > dib.Length)
        {
            // Occasionally GlobalSize has slight slack or tight packing; check minimum viable length
            if (pixelOffset >= dib.Length) return false;
        }

        byte[] bgra = new byte[biWidth * absHeight * 4];
        bool anyNonZeroAlpha = false;

        for (int y = 0; y < absHeight; y++)
        {
            int srcRow = bottomUp ? (absHeight - 1 - y) : y;
            int srcRowOffset = pixelOffset + srcRow * srcStride;
            if (srcRowOffset + biWidth * (biBitCount / 8) > dib.Length)
                break;

            int dstRowOffset = y * biWidth * 4;
            if (biBitCount == 32)
            {
                for (int x = 0; x < biWidth; x++)
                {
                    int s = srcRowOffset + x * 4;
                    int d = dstRowOffset + x * 4;
                    byte b = dib[s];
                    byte g = dib[s + 1];
                    byte r = dib[s + 2];
                    byte a = dib[s + 3];
                    if (a != 0) anyNonZeroAlpha = true;
                    bgra[d] = b;
                    bgra[d + 1] = g;
                    bgra[d + 2] = r;
                    bgra[d + 3] = a;
                }
            }
            else // 24-bit BGR
            {
                for (int x = 0; x < biWidth; x++)
                {
                    int s = srcRowOffset + x * 3;
                    int d = dstRowOffset + x * 4;
                    bgra[d] = dib[s];
                    bgra[d + 1] = dib[s + 1];
                    bgra[d + 2] = dib[s + 2];
                    bgra[d + 3] = 255;
                }
                anyNonZeroAlpha = true;
            }
        }

        // Many Win32 apps put 32-bit GDI bitmaps on clipboard with all-zero alpha channel meaning opaque!
        if (!anyNonZeroAlpha)
        {
            for (int i = 3; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;
            }
        }

        width = biWidth;
        height = absHeight;
        bgraOut = bgra;
        return true;
    }

    public static async Task<CapturedImageResult?> NormalizeAndCreateThumbnailAsync(
        byte[]? rawPng,
        byte[]? rawBgra,
        int width,
        int height,
        string sourceFilePath = "")
    {
        try
        {
            byte[] pngBytes;
            byte[] pixelBgra;
            int w;
            int h;

            if (rawPng != null)
            {
                using var mem = new InMemoryRandomAccessStream();
                await mem.WriteAsync(rawPng.AsBuffer());
                mem.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(mem);
                w = (int)decoder.PixelWidth;
                h = (int)decoder.PixelHeight;

                var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                pixelBgra = provider.DetachPixelData();
                pngBytes = rawPng;
            }
            else if (rawBgra != null && width > 0 && height > 0)
            {
                w = width;
                h = height;
                pixelBgra = rawBgra;
                pngBytes = await EncodeBgraToPngAsync(pixelBgra, w, h);
            }
            else if (!string.IsNullOrEmpty(sourceFilePath) && File.Exists(sourceFilePath))
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(sourceFilePath);
                using var mem = new InMemoryRandomAccessStream();
                await mem.WriteAsync(fileBytes.AsBuffer());
                mem.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(mem);
                w = (int)decoder.PixelWidth;
                h = (int)decoder.PixelHeight;

                var provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                pixelBgra = provider.DetachPixelData();
                pngBytes = Path.GetExtension(sourceFilePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? fileBytes
                    : await EncodeBgraToPngAsync(pixelBgra, w, h);
            }
            else
            {
                return null;
            }

            // Generate crisp thumbnail (max height 200px, max width 520px)
            byte[] thumbPng = await CreateThumbnailPngAsync(pngBytes, w, h, maxWidth: 520, maxHeight: 200);

            return new CapturedImageResult
            {
                PngBytes = pngBytes,
                ThumbnailPngBytes = thumbPng,
                Width = w,
                Height = h,
                SourceFilePath = sourceFilePath
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]> EncodeBgraToPngAsync(byte[] bgra, int width, int height)
    {
        using var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            (uint)width,
            (uint)height,
            96,
            96,
            bgra);
        await encoder.FlushAsync();

        outStream.Seek(0);
        byte[] result = new byte[outStream.Size];
        await outStream.ReadAsync(result.AsBuffer(), (uint)result.Length, InputStreamOptions.None);
        return result;
    }

    private static async Task<byte[]> CreateThumbnailPngAsync(byte[] pngBytes, int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= maxWidth && height <= maxHeight)
            return pngBytes;

        double scale = Math.Min(maxWidth / (double)width, maxHeight / (double)height);
        uint scaledW = (uint)Math.Max(1, Math.Round(width * scale));
        uint scaledH = (uint)Math.Max(1, Math.Round(height * scale));

        using var inStream = new InMemoryRandomAccessStream();
        await inStream.WriteAsync(pngBytes.AsBuffer());
        inStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inStream);
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        byte[] scaledPixels = pixelData.DetachPixelData();
        return await EncodeBgraToPngAsync(scaledPixels, (int)scaledW, (int)scaledH);
    }

    /// <summary>
    /// Converts PNG bytes into BOTH PNG raw bytes AND a classic bottom-up 32-bit CF_DIB memory block so EVERY Windows app can paste seamlessly!
    /// </summary>
    public static async Task<byte[]?> ConvertPngToDibBytesAsync(byte[] pngBytes)
    {
        try
        {
            using var inStream = new InMemoryRandomAccessStream();
            await inStream.WriteAsync(pngBytes.AsBuffer());
            inStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(inStream);
            int w = (int)decoder.PixelWidth;
            int h = (int)decoder.PixelHeight;

            // Premultiply or composite over white ONLY for legacy CF_DIB apps? Actually Premultiplied BGRA works best for CF_DIB 32-bit, while modern apps read our concurrent "PNG" format!
            var provider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] topDownBgra = provider.DetachPixelData();
            int imageBytes = w * h * 4;
            byte[] dib = new byte[40 + imageBytes];

            // BITMAPINFOHEADER (40 bytes)
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), w);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), h); // bottom-up for universal compatibility
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12, 2), 1); // biPlanes
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14, 2), 32); // biBitCount
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16, 4), 0); // BI_RGB
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20, 4), (uint)imageBytes);

            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int srcOffset = (h - 1 - y) * stride;
                int dstOffset = 40 + y * stride;
                Buffer.BlockCopy(topDownBgra, srcOffset, dib, dstOffset, stride);
            }

            return dib;
        }
        catch
        {
            return null;
        }
    }

    public static void SetClipboardBytes(uint format, byte[] data)
    {
        nint hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)data.Length);
        if (hGlobal == 0) return;

        nint ptr = NativeMethods.GlobalLock(hGlobal);
        if (ptr == 0)
        {
            NativeMethods.GlobalFree(hGlobal);
            return;
        }

        Marshal.Copy(data, 0, ptr, data.Length);
        NativeMethods.GlobalUnlock(hGlobal);

        if (NativeMethods.SetClipboardData(format, hGlobal) == 0)
        {
            NativeMethods.GlobalFree(hGlobal);
        }
    }

    public static void WriteDualImageToOpenClipboard(byte[] pngBytes, byte[]? dibBytes)
    {
        SetClipboardBytes(PngClipFormat, pngBytes);
        SetClipboardBytes(MimePngClipFormat, pngBytes);
        if (dibBytes != null)
        {
            SetClipboardBytes(NativeMethods.CF_DIB, dibBytes);
        }
    }

    public static void WriteFileDropListToOpenClipboard(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        // DROPFILES struct (20 bytes) + null-terminated UTF-16 strings + final null char
        int charsCount = 1; // trailing double-null
        foreach (string p in paths)
        {
            charsCount += p.Length + 1;
        }

        int structSize = sizeof(NativeMethods.DROPFILES);
        int totalBytes = structSize + charsCount * 2;

        nint hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (nuint)totalBytes);
        if (hGlobal == 0) return;

        nint ptr = NativeMethods.GlobalLock(hGlobal);
        if (ptr == 0)
        {
            NativeMethods.GlobalFree(hGlobal);
            return;
        }

        try
        {
            var df = (NativeMethods.DROPFILES*)ptr;
            df->pFiles = (uint)structSize;
            df->fWide = 1; // Unicode

            char* dst = (char*)(ptr + structSize);
            int offset = 0;
            foreach (string p in paths)
            {
                p.AsSpan().CopyTo(new Span<char>(dst + offset, p.Length));
                offset += p.Length;
                dst[offset++] = '\0';
            }
            dst[offset] = '\0';
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }

        if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hGlobal) == 0)
        {
            NativeMethods.GlobalFree(hGlobal);
        }
    }

    /// <summary>
    /// Loads a BitmapImage from disk via an in-memory stream so the file on disk is NEVER locked!
    /// This guarantees safe deletion, cache clearing, and cache folder migration at any time.
    /// </summary>
    public static async Task<BitmapImage?> LoadUnlockedBitmapAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath);
            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            ms.Seek(0);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms);
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
