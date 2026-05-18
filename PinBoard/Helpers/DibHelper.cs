using Microsoft.UI.Xaml.Media.Imaging;
using PinBoard.Models;
using Windows.Storage.Streams;

namespace PinBoard.Helpers;

/// Converts clipboard image formats (PNG or CF_DIB) to a BitmapImage for display.
internal static class DibHelper
{
    public static async Task<BitmapImage?> ToBitmapAsync(FormatBundle bundle)
    {
        byte[]? bytes = GetImageBytes(bundle);
        if (bytes is null) return null;

        try
        {
            var ras = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(ras);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            ras.Seek(0);

            var bmp = new BitmapImage
            {
                DecodePixelWidth = 160,
                DecodePixelType  = DecodePixelType.Logical
            };
            await bmp.SetSourceAsync(ras);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? GetImageBytes(FormatBundle bundle)
    {
        if (bundle.Formats.TryGetValue(FormatBundle.FmtPng, out var png))
            return png;

        if (bundle.Formats.TryGetValue(FormatBundle.FmtDib, out var dib))
            return PrependBmpHeader(dib);

        return null;
    }

    private static byte[] PrependBmpHeader(byte[] dib)
    {
        if (dib.Length < 40) return dib;

        uint   biSize     = BitConverter.ToUInt32(dib, 0);
        ushort biBitCount = BitConverter.ToUInt16(dib, 14);
        uint   biClrUsed  = BitConverter.ToUInt32(dib, 32);

        uint colorTableEntries = biClrUsed > 0 ? biClrUsed
            : biBitCount <= 8 ? (uint)(1 << biBitCount) : 0u;
        uint pixelOffset = 14u + biSize + colorTableEntries * 4u;
        uint fileSize    = 14u + (uint)dib.Length;

        var result = new byte[14 + dib.Length];
        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BitConverter.TryWriteBytes(result.AsSpan(2),  fileSize);
        BitConverter.TryWriteBytes(result.AsSpan(10), pixelOffset);
        dib.CopyTo(result, 14);
        return result;
    }
}
