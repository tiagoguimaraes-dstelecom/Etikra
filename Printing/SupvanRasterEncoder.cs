using Etikra.Models;
using Etikra.Services;
using System.IO;
using SevenZip;
using SevenZip.Compression.LZMA;

namespace Etikra.Printing;

public sealed record SupvanPrintData(byte[] Compressed, int BufferCount, ushort Speed, int WidthDots, int HeightDots);

/// <summary>
/// Converts Etikra's rendered label into the 4096-byte buffers understood by
/// SUPVAN firmware, then wraps them in an LZMA1-alone stream.
/// </summary>
public static class SupvanRasterEncoder
{
    private const int PrintBufferSize = 4096;
    private const int HeaderSize = 14;
    private const int MaxImageBytes = 4074;
    private const int MarginDots = 8;

    public static SupvanPrintData Encode(LabelDocument document, PrinterProfile profile, byte density, byte materialType = 1)
    {
        if (materialType > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(materialType), "The verified print-buffer material field is two bits wide.");
        }

        density = Math.Min((byte)15, density);
        var bitmap = LabelRenderer.Render(document, profile.Dpi);
        var rowMajor = LabelRenderer.ToOneBitRows(bitmap);
        var canvas = BuildPrintheadCanvas(rowMajor, bitmap.PixelWidth, bitmap.PixelHeight, profile.PrintheadDots);
        var perLineBytes = profile.PrintheadDots / 8;
        var buffers = BuildPrintBuffers(canvas, perLineBytes, bitmap.PixelHeight, density, materialType);
        var raw = new byte[buffers.Count * PrintBufferSize];
        for (var i = 0; i < buffers.Count; i++)
        {
            Buffer.BlockCopy(buffers[i], 0, raw, i * PrintBufferSize, PrintBufferSize);
        }

        var compressed = CompressLzma(raw);
        if (compressed.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"The compressed label is {compressed.Length:N0} bytes, above this protocol's 65,535-byte page limit. Reduce the label height or image detail.");
        }

        var average = compressed.Length / buffers.Count;
        return new SupvanPrintData(compressed, buffers.Count, CalculateSpeed(average), bitmap.PixelWidth, bitmap.PixelHeight);
    }

    internal static byte[] BuildPrintheadCanvas(byte[] rows, int width, int height, int printheadDots)
    {
        var inputStride = (width + 7) / 8;
        var outputStride = printheadDots / 8;
        var outputWidth = outputStride * 8;
        var output = new byte[outputStride * height];
        var sourceOffset = Math.Max(0, (width - outputWidth) / 2);
        var destinationOffset = Math.Max(0, (outputWidth - width) / 2);
        var copyWidth = Math.Min(width, outputWidth);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < copyWidth; x++)
            {
                var sourceX = sourceOffset + x;
                var sourceSet = (rows[y * inputStride + sourceX / 8] & (1 << (7 - sourceX % 8))) != 0;
                if (sourceSet)
                {
                    var destinationX = destinationOffset + x;
                    output[y * outputStride + destinationX / 8] |= (byte)(1 << (destinationX % 8));
                }
            }
        }

        return output;
    }

    internal static IReadOnlyList<byte[]> BuildPrintBuffers(byte[] image, int perLineBytes, int totalColumns, byte density, byte materialType = 1)
    {
        if (perLineBytes is <= 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(perLineBytes));
        }

        if (totalColumns <= MarginDots * 2 || totalColumns > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(totalColumns));
        }

        var maxColumns = MaxImageBytes / perLineBytes;
        var imageColumns = totalColumns - MarginDots * 2;
        var result = new List<byte[]>();
        var currentColumn = 0;
        while (currentColumn < imageColumns)
        {
            var columns = Math.Min(maxColumns, imageColumns - currentColumn);
            var first = currentColumn == 0;
            var last = currentColumn + columns >= imageColumns;
            var start = (MarginDots + currentColumn) * perLineBytes;
            var dataLength = columns * perLineBytes;
            var buffer = BuildPrintBuffer(image.AsSpan(start, dataLength), (byte)perLineBytes, (ushort)columns, first, last, density, materialType);
            result.Add(buffer);
            currentColumn += columns;
        }

        return result;
    }

    internal static byte[] BuildPrintBuffer(
        ReadOnlySpan<byte> image,
        byte perLineBytes,
        ushort columns,
        bool first,
        bool last,
        byte density,
        byte materialType = 1)
    {
        var buffer = new byte[PrintBufferSize];
        buffer[2] = (byte)((first ? 0x02 : 0) | (last ? 0x04 | 0x08 : 0));
        buffer[3] = (byte)(((materialType & 0x03) << 6) | (Math.Min((byte)15, density) << 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), columns);
        buffer[6] = perLineBytes;
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 2), (ushort)MarginDots);
        BitConverter.TryWriteBytes(buffer.AsSpan(10, 2), (ushort)MarginDots);
        buffer[12] = Math.Min((byte)15, density);
        image[..Math.Min(image.Length, PrintBufferSize - HeaderSize)].CopyTo(buffer.AsSpan(HeaderSize));

        var dataEnd = HeaderSize + columns * perLineBytes;
        uint checksum = 0;
        for (var i = 2; i < HeaderSize; i++)
        {
            checksum += buffer[i];
        }

        var strides = dataEnd / 256;
        for (var i = 1; i <= strides; i++)
        {
            checksum += buffer[i * 256 - 1];
        }

        BitConverter.TryWriteBytes(buffer.AsSpan(0, 2), (ushort)checksum);
        return buffer;
    }

    internal static byte[] CompressLzma(byte[] data)
    {
        var encoder = new Encoder();
        encoder.SetCoderProperties(
            [CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitContextBits, CoderPropID.LitPosBits, CoderPropID.Algorithm, CoderPropID.NumFastBytes, CoderPropID.MatchFinder, CoderPropID.EndMarker],
            [8192, 2, 3, 0, 2, 128, "bt4", true]);
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        encoder.WriteCoderProperties(output);
        output.Write(BitConverter.GetBytes((long)data.Length));
        encoder.Code(input, output, data.Length, -1, null);

        var compressed = output.ToArray();
        if (compressed.Length < 13)
        {
            throw new InvalidDataException("The LZMA encoder returned an invalid stream.");
        }

        // The printer expects LZMA1-alone: properties, 8 KiB dictionary, and exact raw size.
        compressed[0] = 0x5D;
        BitConverter.TryWriteBytes(compressed.AsSpan(1, 4), 8192u);
        BitConverter.TryWriteBytes(compressed.AsSpan(5, 8), (ulong)data.Length);
        return compressed;
    }

    internal static ushort CalculateSpeed(int averageCompressedBytes) => averageCompressedBytes switch
    {
        > 3000 => 10,
        > 2800 => 15,
        > 2500 => 20,
        > 2000 => 25,
        > 1500 => 40,
        > 1000 => 45,
        > 500 => 55,
        _ => 60
    };
}
